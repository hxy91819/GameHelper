using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Offscreen-renderable console dialogs, independent of desktop theme and runtime commands.</summary>
internal static class ConsoleOverlayVisuals
{
    internal static readonly SolidColorBrush Selected = ConsoleModeVisuals.Brush("#30273F");
    private static readonly SolidColorBrush PrimaryText = ConsoleModeVisuals.Brush("#191320");

    internal static Border Surface(StackPanel content, string? hint = null, string? status = null)
    {
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        var body = new DockPanel();
        if (!string.IsNullOrWhiteSpace(hint) || !string.IsNullOrWhiteSpace(status))
        {
            var messages = new StackPanel();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var feedback = ConsoleModeVisuals.Label(status, 17, ConsoleModeVisuals.Accent);
                feedback.Margin = new Thickness(0, 0, 0, 8); messages.Children.Add(feedback);
            }
            if (!string.IsNullOrWhiteSpace(hint)) messages.Children.Add(ConsoleModeVisuals.Label(hint, 16, ConsoleModeVisuals.Secondary));
            var help = new Border { Child = messages,
                BorderBrush = ConsoleModeVisuals.Stroke, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(help, Dock.Bottom); body.Children.Add(help);
        }
        body.Children.Add(scroll);
        var frame = ConsoleModeVisuals.PanelFrame(body, 26);
        frame.MaxWidth = 880; frame.HorizontalAlignment = HorizontalAlignment.Stretch;
        frame.VerticalAlignment = VerticalAlignment.Center;
        return frame;
    }

    internal static void Header(StackPanel panel, string title, string description)
    {
        var eyebrow = ConsoleModeVisuals.Label("GAMEHELPER / 主机模式", 14, ConsoleModeVisuals.Accent);
        eyebrow.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(eyebrow);
        var heading = ConsoleModeVisuals.Label(title, 32); heading.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(heading);
        var subtitle = ConsoleModeVisuals.Label(description, 18, ConsoleModeVisuals.Secondary);
        subtitle.Margin = new Thickness(0, 0, 0, 20); panel.Children.Add(subtitle);
    }

    internal static Border Notice(string text, bool warning = false)
    {
        var label = ConsoleModeVisuals.Label(text, 17, warning ? ConsoleModeVisuals.Brush("#F2B7AE") : ConsoleModeVisuals.Secondary);
        var frame = ConsoleModeVisuals.PanelFrame(label, 14); frame.Background = ConsoleModeVisuals.Surface;
        frame.Margin = new Thickness(0, 12, 0, 0); return frame;
    }

    internal static Button Row(string label, string? description, string? value, bool selected,
        bool enabled, Action click, bool primary = false)
    {
        var emphasized = primary && enabled;
        var foreground = !enabled ? ConsoleModeVisuals.Secondary : emphasized ? PrimaryText : ConsoleModeVisuals.Text;
        var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(ConsoleModeVisuals.Label(label, 22, foreground));
        if (!string.IsNullOrEmpty(description))
        {
            var detail = ConsoleModeVisuals.Label(description, 16, emphasized ? PrimaryText : ConsoleModeVisuals.Secondary);
            detail.Margin = new Thickness(0, 5, 0, 0); copy.Children.Add(detail);
        }
        content.Children.Add(copy);
        if (value is not null)
        {
            var pill = new Border { Background = emphasized ? ConsoleModeVisuals.Brush("#D9C8F4") : ConsoleModeVisuals.Surface,
                CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, Child = ConsoleModeVisuals.Label(value, 19,
                    !enabled ? ConsoleModeVisuals.Secondary : emphasized ? PrimaryText : ConsoleModeVisuals.Accent) };
            Grid.SetColumn(pill, 1); content.Children.Add(pill);
        }
        var button = new Button { Content = content, Style = ConsoleModeVisuals.ButtonStyle(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 13, 18, 13), Margin = new Thickness(0, 0, 0, 9), MinHeight = 62,
            Background = !enabled ? ConsoleModeVisuals.Surface : emphasized ? ConsoleModeVisuals.Accent : selected ? Selected : ConsoleModeVisuals.Raised,
            Foreground = foreground, BorderBrush = selected ? ConsoleModeVisuals.Accent : ConsoleModeVisuals.Stroke, IsEnabled = enabled };
        if (selected) button.Loaded += (_, _) => button.BringIntoView();
        button.Click += (_, _) => click(); return button;
    }

    internal static Button Key(string text, bool selected, bool enabled, Action click)
    {
        var button = Row(text, null, null, selected, enabled, click);
        button.Content = ConsoleModeVisuals.Label(text, 21, enabled ? ConsoleModeVisuals.Text : ConsoleModeVisuals.Secondary);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Padding = new Thickness(3, 11, 3, 11); button.MinHeight = 50; button.Margin = new Thickness(3);
        return button;
    }
}
