using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Five explicit rating actions. Binding, focus and preview never submit.</summary>
internal sealed class SessionRatingStars : UniformGrid
{
    private static readonly DependencyProperty StarsProperty = DependencyProperty.Register(
        "Stars", typeof(int), typeof(SessionRatingStars), new PropertyMetadata(0, OnStarsChanged));
    private static readonly DependencyProperty BusyProperty = DependencyProperty.Register(
        "Busy", typeof(bool), typeof(SessionRatingStars), new PropertyMetadata(false, (d, e) => d.SetValue(IsEnabledProperty, !(bool)e.NewValue)));
    private readonly Button[] buttons = new Button[5];
    private int? preview;
    internal event Action<int>? StarClicked;

    internal SessionRatingStars(SessionRatingEditorViewModel model, bool console = false)
    {
        Rows = 1; Columns = 5; MaxWidth = console ? 420 : 340;
        HorizontalAlignment = HorizontalAlignment.Left;
        // Explicit style keeps the focus indicator and star contrast independent of shell button styles.
        var chrome = new FrameworkElementFactory(typeof(Border)); chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); chrome.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty,
            console ? ConsoleModeVisuals.Accent : new DynamicResourceExtension("FocusStrong"), "Chrome"));
        template.Triggers.Add(focus);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .55)); template.Triggers.Add(disabled);
        var style = new Style(typeof(Button)); style.Setters.Add(new Setter(Control.TemplateProperty, template));
        for (var i = 0; i < buttons.Length; i++)
        {
            int stars = i + 1;
            var button = new Button { Tag = stars, Content = "☆", Style = style,
                FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = console ? 46 : 38,
                Width = console ? 78 : 60, Height = console ? 76 : 60, Margin = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = $"{stars} 星，点击保存" };
            if (console) button.Foreground = ConsoleModeVisuals.Accent;
            else button.SetResourceReference(Control.ForegroundProperty, "Accent");
            AutomationProperties.SetName(button, $"{stars} 星");
            button.MouseEnter += (_, _) => { preview = stars; Refresh(); };
            button.MouseLeave += (_, _) => { preview = null; Refresh(); };
            button.Click += (_, _) => { if (!IsEnabled || model.Busy) return; preview = null; StarClicked?.Invoke(stars); Refresh(); };
            buttons[i] = button; Children.Add(button);
        }
        SetBinding(StarsProperty, new Binding(nameof(model.Stars)) { Source = model, Mode = BindingMode.OneWay });
        SetBinding(BusyProperty, new Binding(nameof(model.Busy)) { Source = model, Mode = BindingMode.OneWay });
        Refresh();
    }

    private static void OnStarsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SessionRatingStars)d).Refresh();
    private void Refresh()
    {
        int stars = preview ?? (int)GetValue(StarsProperty);
        for (int i = 0; i < buttons.Length; i++)
            if (buttons[i] is { } button) button.Content = i < stars ? "★" : "☆";
    }
}
