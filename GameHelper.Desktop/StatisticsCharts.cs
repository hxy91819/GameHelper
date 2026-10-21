using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal static class StatisticsDrawing
{
    public static readonly Brush Muted = Brush("#8F9EB5");
    public static Brush Brush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    public static void Text(FrameworkElement owner, DrawingContext dc, string text, double x, double y, double size, Brush brush)
        => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip), new Point(x, y));
}

public sealed class PlaytimeTrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<DashboardBucket>),
        typeof(PlaytimeTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<DashboardBucket>? Points { get => (IReadOnlyList<DashboardBucket>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    private const double Left = 44;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var points = Points;
        if (points is null || points.Count == 0 || ActualWidth < 80 || ActualHeight < 60) return;
        var plotHeight = ActualHeight - 27;
        var max = Math.Max(60, points.Max(point => point.Minutes));
        var ceiling = Math.Ceiling(max / 60d) * 60;
        for (var level = 0; level <= 3; level++)
        {
            var y = plotHeight * level / 3;
            dc.DrawLine(new Pen(StatisticsDrawing.Brush("#2B3548"), 1), new Point(Left, y), new Point(ActualWidth, y));
            StatisticsDrawing.Text(this, dc, $"{ceiling / 60 * (3 - level) / 3:0.#}h", 0, Math.Max(0, y - 8), 10, StatisticsDrawing.Muted);
        }
        var pitch = (ActualWidth - Left) / points.Count;
        var width = Math.Max(2, Math.Min(25, pitch * .65));
        var gradient = new LinearGradientBrush(Color.FromRgb(159, 177, 255), Color.FromRgb(92, 111, 213), 90);
        for (var index = 0; index < points.Count; index++)
        {
            var height = points[index].Minutes / ceiling * plotHeight;
            if (height > 0) dc.DrawRoundedRectangle(index == points.Count - 1 ? StatisticsDrawing.Brush("#7EDDB5") : gradient, null,
                new Rect(Left + index * pitch + (pitch - width) / 2, plotHeight - height, width, height), 3, 3);
            var step = Math.Max(1, (int)Math.Ceiling(points.Count / 7d));
            if (index % step == 0 || index == points.Count - 1)
                StatisticsDrawing.Text(this, dc, points[index].Label, Math.Min(ActualWidth - 32, Left + index * pitch), plotHeight + 9, 10, StatisticsDrawing.Muted);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Points is not { Count: > 0 } points || ActualWidth <= Left) return;
        var x = e.GetPosition(this).X;
        var index = (int)((x - Left) / ((ActualWidth - Left) / points.Count));
        ToolTip = x >= Left && index >= 0 && index < points.Count
            ? $"{points[index].Start:yyyy/MM/dd} — {points[index].End:yyyy/MM/dd}\n{(points[index].Minutes == 0 ? "0 分钟" : StatisticsViewModel.Duration(points[index].Minutes))}" : null;
    }
}

public sealed class PlaytimeCalendar : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(nameof(Days), typeof(IReadOnlyList<DashboardDay>),
        typeof(PlaytimeCalendar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<DashboardDay>? Days { get => (IReadOnlyList<DashboardDay>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    private double Pitch => Math.Min(19, (ActualWidth - 34) / 53);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
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
            var color = day.Sessions == 0 ? "#263143" : day.Minutes < 30 ? "#285B55" : day.Minutes < 90 ? "#388875" : day.Minutes < 180 ? "#53B594" : "#91E5BC";
            dc.DrawRoundedRectangle(StatisticsDrawing.Brush(color), null,
                new Rect(30 + column * Pitch, 23 + row * Pitch, Pitch - 3, Pitch - 3), 2, 2);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (Days is not { Count: > 0 } days || Pitch <= 0) return;
        var column = (int)Math.Floor((point.X - 30) / Pitch);
        var row = (int)Math.Floor((point.Y - 23) / Pitch);
        var index = column * 7 + row;
        ToolTip = column >= 0 && row is >= 0 and < 7 && index >= 0 && index < days.Count
            ? $"{days[index].Date:yyyy/MM/dd dddd}\n{days[index].Sessions} 次游玩 · {(days[index].Sessions == 0 ? "0 分钟" : StatisticsViewModel.Duration(days[index].Minutes))}" : null;
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
        var radius = Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 7);
        dc.DrawEllipse(null, new Pen(StatisticsDrawing.Brush("#303B52"), 7), center, radius, radius);
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
            dc.DrawGeometry(null, new Pen(StatisticsDrawing.Brush("#A9B4FF"), 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }
        var text = $"{share:P0}";
        StatisticsDrawing.Text(this, dc, text, center.X - (text.Length * 5.5), center.Y - 13, 20, StatisticsDrawing.Brush("#EAF0FF"));
    }
}
