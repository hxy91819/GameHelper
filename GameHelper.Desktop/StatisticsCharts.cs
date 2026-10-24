using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal static class StatisticsDrawing
{
    public static Brush Muted => Brush("Muted");
    public static Brush Brush(string key) => ThemeBrushes.Get(key);
    public static void Text(FrameworkElement owner, DrawingContext dc, string text, double x, double y, double size, Brush brush)
        => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip), new Point(x, y));
}

/// <summary>Hover popup for custom-drawn charts. FrameworkElement.ToolTip only opens on mouse enter, so the charts drive this popup themselves.</summary>
internal sealed class ChartTooltip
{
    private readonly Popup _popup = new() { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private readonly Border _bubble;
    private readonly TextBlock _text;
    private bool _open;
    private Window? _window;

    public ChartTooltip()
    {
        _text = new TextBlock { Foreground = ThemeBrushes.Get("TextPrimary"), FontSize = 12 };
        _bubble = new Border
        {
            Background = StatisticsDrawing.Brush("TooltipSurface"),
            BorderBrush = StatisticsDrawing.Brush("StrongBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Opacity = .35, Color = Colors.Black },
            Child = _text,
            IsHitTestVisible = false
        };
        _popup.Child = _bubble;
    }

    public void Show(FrameworkElement owner, string text, Point position)
    {
        _text.Foreground = ThemeBrushes.Get("TextPrimary");
        _bubble.Background = ThemeBrushes.Get("TooltipSurface");
        _bubble.BorderBrush = ThemeBrushes.Get("StrongBorder");
        _text.Text = text;
        _popup.PlacementTarget = owner;
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _bubble.DesiredSize;
        var limitWidth = owner.ActualWidth;
        var limitHeight = owner.ActualHeight;
        if (Window.GetWindow(owner) is { } window)
        {
            if (!ReferenceEquals(_window, window))
            {
                if (_window is not null) _window.Deactivated -= OnOwnerDeactivated;
                _window = window;
                window.Deactivated += OnOwnerDeactivated;
            }
            var origin = owner.TranslatePoint(new Point(0, 0), window);
            limitWidth = window.ActualWidth - origin.X;
            limitHeight = window.ActualHeight - origin.Y;
        }
        var x = position.X + 14;
        if (x + size.Width > limitWidth - 4) x = position.X - size.Width - 14;
        var y = position.Y + 18;
        if (y + size.Height > limitHeight - 4) y = position.Y - size.Height - 12;
        _popup.HorizontalOffset = Math.Max(0, x);
        _popup.VerticalOffset = Math.Max(0, y);
        if (_open) return;
        _popup.IsOpen = true;
        _open = true;
    }

    public void Hide()
    {
        if (!_open) return;
        _popup.IsOpen = false;
        _open = false;
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => Hide();
}

public sealed class PlaytimeTrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<DashboardBucket>),
        typeof(PlaytimeTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));
    public IReadOnlyList<DashboardBucket>? Points { get => (IReadOnlyList<DashboardBucket>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    private const double Left = 44;
    private readonly ChartTooltip _tooltip = new();

    public PlaytimeTrendChart()
    {
        Unloaded += (_, _) => _tooltip.Hide();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _tooltip.Hide(); };
    }

    private static void OnPointsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((PlaytimeTrendChart)target)._tooltip.Hide();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var points = Points;
        if (points is null || points.Count == 0 || ActualWidth < 80 || ActualHeight < 60) return;
        var plotHeight = ActualHeight - 27;
        var max = Math.Max(60, points.Max(point => point.Minutes));
        var ceiling = Math.Ceiling(max / 60d) * 60;
        for (var level = 0; level <= 3; level++)
        {
            var y = plotHeight * level / 3;
            dc.DrawLine(new Pen(StatisticsDrawing.Brush("ChartGrid"), 1), new Point(Left, y), new Point(ActualWidth, y));
            StatisticsDrawing.Text(this, dc, $"{ceiling / 60 * (3 - level) / 3:0.#}h", 0, Math.Max(0, y - 8), 10, StatisticsDrawing.Muted);
        }
        var pitch = (ActualWidth - Left) / points.Count;
        var width = Math.Max(2, Math.Min(25, pitch * .65));
        var gradient = new LinearGradientBrush(ThemeBrushes.Color("Accent"), ThemeBrushes.Color("ChartBarEnd"), 90);
        for (var index = 0; index < points.Count; index++)
        {
            var height = points[index].Minutes / ceiling * plotHeight;
            if (height > 0) dc.DrawRoundedRectangle(index == points.Count - 1 ? StatisticsDrawing.Brush("ChartRecent") : gradient, null,
                new Rect(Left + index * pitch + (pitch - width) / 2, plotHeight - height, width, height), 3, 3);
            var step = Math.Max(1, (int)Math.Ceiling(points.Count / 7d));
            if (index % step == 0 || index == points.Count - 1)
                StatisticsDrawing.Text(this, dc, points[index].Label, Math.Min(ActualWidth - 32, Left + index * pitch), plotHeight + 9, 10, StatisticsDrawing.Muted);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (Points is not { Count: > 0 } points || ActualWidth < 80 || ActualHeight < 60
            || position.X < Left || position.Y > ActualHeight - 27)
        {
            _tooltip.Hide();
            return;
        }
        var index = (int)((position.X - Left) / ((ActualWidth - Left) / points.Count));
        if (index < 0 || index >= points.Count) { _tooltip.Hide(); return; }
        var point = points[index];
        var label = point.Start == point.End ? $"{point.Start:yyyy/MM/dd}" : $"{point.Start:yyyy年M月}";
        _tooltip.Show(this, $"{label}\n{(point.Minutes == 0 ? "无游玩记录" : StatisticsViewModel.Duration(point.Minutes))}", position);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tooltip.Hide();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        _tooltip.Hide();
    }
}

public sealed class PlaytimeCalendar : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(nameof(Days), typeof(IReadOnlyList<DashboardDay>),
        typeof(PlaytimeCalendar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDaysChanged));
    public IReadOnlyList<DashboardDay>? Days { get => (IReadOnlyList<DashboardDay>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    private readonly ChartTooltip _tooltip = new();
    private int _hovered = -1;

    public PlaytimeCalendar()
    {
        Unloaded += (_, _) => _tooltip.Hide();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _tooltip.Hide(); };
    }

    private static void OnDaysChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var calendar = (PlaytimeCalendar)target;
        calendar.SetHovered(-1);
        calendar._tooltip.Hide();
    }

    private double Pitch => Math.Min(19, (ActualWidth - 34) / 53);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Days is not { Count: > 0 } days || Pitch < 3) return;
        for (var weekday = 0; weekday < 7; weekday += 2)
            StatisticsDrawing.Text(this, dc, new[] { "一", "二", "三", "四", "五", "六", "日" }[weekday], 0, 23 + weekday * Pitch, 9, StatisticsDrawing.Muted);
        var lastMonth = -1;
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var column = index / 7;
            var row = index % 7;
            if (row == 0 && day.Date.Month != lastMonth)
            { StatisticsDrawing.Text(this, dc, day.Date.ToString("M月"), 30 + column * Pitch, 0, 10, StatisticsDrawing.Muted); lastMonth = day.Date.Month; }
            var color = day.Sessions == 0 ? "CalendarZero" : day.Minutes < 30 ? "CalendarOne" : day.Minutes < 90 ? "CalendarTwo" : day.Minutes < 180 ? "CalendarThree" : "CalendarFour";
            dc.DrawRoundedRectangle(StatisticsDrawing.Brush(color), null,
                new Rect(30 + column * Pitch, 23 + row * Pitch, Pitch - 3, Pitch - 3), 2, 2);
        }
        if (_hovered >= 0 && _hovered < days.Count)
            dc.DrawRoundedRectangle(null, new Pen(StatisticsDrawing.Brush("CalendarFocus"), 1.5),
                new Rect(30 + _hovered / 7 * Pitch - 1, 23 + _hovered % 7 * Pitch - 1, Pitch - 1, Pitch - 1), 3, 3);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pitch = Pitch;
        var position = e.GetPosition(this);
        var column = (int)Math.Floor((position.X - 30) / pitch);
        var row = (int)Math.Floor((position.Y - 23) / pitch);
        var index = column * 7 + row;
        if (Days is not { Count: > 0 } days || pitch < 3 || column < 0 || row is < 0 or > 6 || index < 0 || index >= days.Count)
        {
            SetHovered(-1);
            _tooltip.Hide();
            return;
        }
        SetHovered(index);
        var day = days[index];
        _tooltip.Show(this, $"{day.Date:yyyy/MM/dd ddd}\n{(day.Sessions == 0 ? "无游玩记录" : $"{day.Sessions} 次 · {StatisticsViewModel.Duration(day.Minutes)}")}", position);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(-1);
        _tooltip.Hide();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        SetHovered(-1);
        _tooltip.Hide();
    }

    private void SetHovered(int index)
    {
        if (_hovered == index) return;
        _hovered = index;
        InvalidateVisual();
    }
}

public sealed class PlaytimeShareRing : FrameworkElement
{
    public static readonly DependencyProperty ShareProperty = DependencyProperty.Register(nameof(Share), typeof(double), typeof(PlaytimeShareRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Share { get => (double)GetValue(ShareProperty); set => SetValue(ShareProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var share = double.IsFinite(Share) ? Math.Clamp(Share, 0, 1) : 0;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var extent = Math.Min(ActualWidth, ActualHeight);
        var stroke = Math.Clamp(extent * .08, 4, 7);
        var radius = Math.Max(1, extent / 2 - stroke);
        dc.DrawEllipse(null, new Pen(StatisticsDrawing.Brush("RingTrack"), stroke), center, radius, radius);
        if (share > 0)
        {
            var angle = Math.Min(.99999, share) * Math.PI * 2 - Math.PI / 2;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                context.ArcTo(new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius),
                    new Size(radius, radius), 0, share > .5, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, new Pen(StatisticsDrawing.Brush("Accent"), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }
        var text = new FormattedText($"{share:P0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
            Math.Clamp(extent * .24, 11, 20), StatisticsDrawing.Brush("TextPrimary"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
