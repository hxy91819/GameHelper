using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>On-demand session pages, shared by the archive and the game's rating timeline.</summary>
public sealed class SessionReviewWindow : Window
{
    private readonly IGameLibrary _library;
    private readonly string? _dataKey;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _previous = new() { Content = "上一页" };
    private readonly Button _next = new() { Content = "下一页" };
    private readonly RatingTrend _trend = new() { Height = 150 };
    private readonly Stack<long?> _back = new();
    private readonly CheckBox _ratedOnly = new() { Content = "只看已评分", Margin = new(12, 0, 0, 0) };
    private long? _cursor;
    private long? _nextCursor;
    private bool _busy;
    public SessionReviewWindow(IGameLibrary library, string? dataKey = null)
    {
        _library = library; _dataKey = dataKey;
        Title = dataKey is null ? "游玩档案 · 会话回顾与补评" : dataKey + " · 评分随时间变化";
        Width = 680; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Page"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new DockPanel { Margin = new(24) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 0) };
        controls.Children.Add(_previous); controls.Children.Add(_next); controls.Children.Add(_ratedOnly); DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
        DockPanel.SetDock(_status, Dock.Top); panel.Children.Add(_status);
        DockPanel.SetDock(_trend, Dock.Top); panel.Children.Add(_trend);
        panel.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = panel;
        _previous.Click += async (_, _) => { if (_busy || _back.Count == 0) return; _cursor = _back.Pop(); await LoadAsync(); };
        _next.Click += async (_, _) => { if (_busy || _nextCursor is null) return; _back.Push(_cursor); _cursor = _nextCursor; await LoadAsync(); };
        _ratedOnly.Click += async (_, _) => { _cursor = null; _back.Clear(); await LoadAsync(); };
        Loaded += async (_, _) => await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (_busy) return; _busy = true; _previous.IsEnabled = _next.IsEnabled = _ratedOnly.IsEnabled = false;
        try
        {
            var response = await _library.ExecuteAsync(new() { Operation = GameLibraryOperation.SessionReviews,
                SessionReviews = new(_dataKey, _cursor, 30, _ratedOnly.IsChecked == true) });
            if (!response.Success || response.SessionReviews is null) throw new InvalidOperationException(response.Error);
            var page = response.SessionReviews; _nextCursor = page.NextSequence;
            _rows.Children.Clear();
            if (_dataKey is not null)
            {
                var timeline = await _library.ExecuteAsync(new() { Operation = GameLibraryOperation.RatingTimeline, DataKey = _dataKey });
                if (!timeline.Success) throw new InvalidOperationException(timeline.Error);
                _trend.Sessions = timeline.RatingTimeline;
            }
            else _trend.Sessions = page.Sessions.Where(s => s.Rating is not null).Select(s => new SessionRatingPoint(s.SessionId, s.End, s.Rating!.Stars)).ToList();
            _trend.InvalidateVisual();
            _status.Text = (_dataKey is null ? "曲线显示本页 · " : "曲线显示该游戏全部评分 · ") + "按记录顺序分组浏览，每页 30 局 · 评分曲线按本地结束时间排列 · 想法仅在本机展示";
            if (page.Sessions.Count == 0) _status.Text = "暂无会话记录。";
            foreach (var session in page.Sessions)
            {
                var local = session.End.Kind == DateTimeKind.Utc ? session.End.ToLocalTime() : session.End;
                var box = new StackPanel { Margin = new(0, 8, 0, 12) };
                box.Children.Add(new TextBlock { Text = $"{local:yyyy-MM-dd HH:mm} · {session.DataKey} · {session.Minutes} 分钟", TextWrapping = TextWrapping.Wrap });
                var button = new Button { Content = session.Rating is { } rating ? new string('★', rating.Stars) + " · 修改评分" : "补评", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 0) };
                button.Click += async (_, _) =>
                {
                    new SessionRatingWindow(_library, session) { Owner = this }.ShowDialog();
                    await LoadAsync();
                };
                box.Children.Add(button);
                if (session.Rating?.Thought is { } thought) box.Children.Add(new TextBlock { Text = thought, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) });
                _rows.Children.Add(box);
            }
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; _previous.IsEnabled = _back.Count > 0; _next.IsEnabled = _nextCursor is not null; _ratedOnly.IsEnabled = true; }
    }
}

internal sealed class RatingTrend : FrameworkElement
{
    private static DateTime Local(DateTime date) => date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
    public IReadOnlyList<SessionRatingPoint> Sessions { get; set; } = [];
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var brush = TryFindResource("TextPrimary") as Brush ?? Brushes.Gray;
        var pen = new Pen(brush, 1);
        var points = Sessions.OrderBy(s => Local(s.End)).ToList();
        var width = Math.Max(1, ActualWidth - 40); var height = Math.Max(1, ActualHeight - 40);
        var first = points.Count == 0 ? 0 : Local(points[0].End).Ticks;
        var span = Math.Max(1, (points.Count == 0 ? 0 : Local(points[^1].End).Ticks) - first);
        for (var star = 1; star <= 5; star++)
        {
            var y = 12 + (5 - star) * height / 4;
            dc.DrawText(new FormattedText(star.ToString(), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(0, y - 6));
            dc.DrawLine(new Pen(brush, .2), new Point(25, y), new Point(25 + width, y));
        }
        if (points.Count > 0)
        {
            DrawDate(Local(points[0].End).ToString("yyyy-MM-dd"), 25);
            if (points.Count > 1) DrawDate(Local(points[^1].End).ToString("yyyy-MM-dd"), Math.Max(25, ActualWidth - 90));
        }
        void DrawDate(string text, double x) => dc.DrawText(new FormattedText(text,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, 20 + height));
        Point? previous = null;
        foreach (var session in points)
        {
            var point = new Point(25 + width * (Local(session.End).Ticks - first) / span, 12 + (5 - session.Stars) * height / 4);
            if (previous is { } from) dc.DrawLine(pen, from, point);
            dc.DrawEllipse(brush, null, point, 3, 3); previous = point;
        }
    }
}
