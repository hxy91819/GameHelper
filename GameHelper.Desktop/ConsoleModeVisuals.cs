using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Console-only presentation tokens; styles are created per window/dispatcher.</summary>
internal static class ConsoleModeVisuals
{
    internal static readonly SolidColorBrush Surface = Brush("#101116");
    internal static readonly SolidColorBrush Panel = Brush("#191A22");
    internal static readonly SolidColorBrush Raised = Brush("#222330");
    internal static readonly SolidColorBrush Stroke = Brush("#3A3548");
    internal static readonly SolidColorBrush Text = Brush("#F3F0F7");
    internal static readonly SolidColorBrush Secondary = Brush("#B7B0C4");
    internal static readonly SolidColorBrush Accent = Brush("#BCA3E5");
    internal static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }
    internal static TextBlock Label(string text, double size = 18, Brush? foreground = null) => new()
    { Text = text, FontSize = size, Foreground = foreground ?? Text, TextWrapping = TextWrapping.Wrap };

    internal static Border PanelFrame(UIElement child, double padding = 20) => new()
    { Child = child, Background = Panel, BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(padding) };

    internal static Style ButtonStyle()
    {
        var chrome = new FrameworkElementFactory(typeof(Border)); chrome.Name = "Chrome";
        chrome.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        chrome.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        chrome.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1)); chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        foreach (var property in new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusWithinProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Chrome")); template.Triggers.Add(trigger);
        }
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, Secondary));
        disabled.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Arrow)); template.Triggers.Add(disabled);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .88, "Chrome"));
        pressed.Setters.Add(new Setter(UIElement.RenderTransformProperty, new TranslateTransform(0, 1), "Chrome"));
        template.Triggers.Add(pressed);
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Raised)); style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Stroke)); style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 10, 18, 10)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 18d)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        return style;
    }
}
