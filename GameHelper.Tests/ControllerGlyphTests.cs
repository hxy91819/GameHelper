using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameHelper.Desktop;
using GameHelper.Desktop.Input;
using static GameHelper.Desktop.ControllerButton;

namespace GameHelper.Tests;

public sealed class ControllerGlyphTests
{
    [Fact]
    public Task PromptsFollowControllerAndReuseFrozenArtworkAcrossDispatchers() => OnSta(() =>
    {
        var bar = new ControllerHintBar();
        ControllerHint[] hints = [new("详情", Confirm), new("游戏设置", Auxiliary)];
        bar.Set(ControllerKind.PlayStation, hints);
        var firstGroup = bar.Children[0];
        bar.Set(ControllerKind.PlayStation, new("详情", Confirm), new("游戏设置", Auxiliary));
        Assert.Same(firstGroup, bar.Children[0]);
        Assert.Equal("交叉键", AutomationProperties.GetName(Descendants<Image>(bar).First()));
        var psSource = (DrawingImage)Descendants<Image>(bar).First().Source;
        Assert.True(psSource.IsFrozen);
        // The four PS face symbols are actual geometries, not font glyph drawings.
        foreach (var button in new[] { Confirm, Back, Auxiliary, Search })
        {
            var drawing = (DrawingGroup)((DrawingImage)ControllerGlyphs.Create(ControllerKind.PlayStation, button).Source).Drawing;
            Assert.All(drawing.Children, child => Assert.IsType<GeometryDrawing>(child));
        }
        bar.Set(ControllerKind.Xbox, hints);
        Assert.Equal("A", AutomationProperties.GetName(Descendants<Image>(bar).First()));
        Assert.NotSame(psSource, Descendants<Image>(bar).First().Source);
        bar.Set(ControllerKind.PlayStation, hints);
        Assert.Same(psSource, Descendants<Image>(bar).First().Source);
    });

    [Theory]
    [InlineData(ControllerKind.PlayStation, 960, 1)]
    [InlineData(ControllerKind.PlayStation, 1280, 2)]
    [InlineData(ControllerKind.PlayStation, 1920, 1)]
    [InlineData(ControllerKind.Xbox, 960, 1)]
    public Task IconsAndLabelsStayTogetherAndOverlayHintsRemainVisible(ControllerKind kind, int width, int scale) => OnSta(() =>
    {
        var panel = new StackPanel();
        ConsoleOverlayVisuals.Header(panel, kind == ControllerKind.PlayStation ? "PlayStation 按键提示" : "Xbox 按键提示", "矢量图标 · 按键与说明成组换行");
        var catalog = new ControllerHintBar { Margin = new Thickness(0, 0, 0, 22) };
        catalog.Set(kind, Enum.GetValues<ControllerButton>().Select(button => new ControllerHint(ControllerGlyphs.Name(kind, button), button)).ToArray());
        panel.Children.Add(catalog);
        var home = new ControllerHintBar();
        home.Set(kind, new("浏览", Horizontal), new("翻页", LeftTrigger, RightTrigger), new("详情", Confirm), new("游戏设置", Auxiliary),
            new("退出主机模式", Back), new("搜索", Search), new("页签", LeftShoulder, RightShoulder), new("系统菜单", ControllerButton.Menu));
        panel.Children.Add(home);
        var chord = new ControllerHintBar { FontSize = 13, Foreground = ConsoleModeVisuals.Secondary, Margin = new Thickness(0, 10, 0, 15) };
        chord.Set(kind, new ControllerHint("全局切换主机 / 桌面", ModeModifier, ControllerButton.Menu) { Chord = true });
        panel.Children.Add(chord);
        for (var i = 0; i < 10; i++) panel.Children.Add(ConsoleOverlayVisuals.Row("测试设置 " + i, "较长的内容独立滚动，底部提示固定可见。", null, false, true, () => { }));
        var hints = new ControllerHintBar { Foreground = ConsoleModeVisuals.Secondary };
        hints.Set(kind, new("选择", Vertical), new("调整", Horizontal), new("切换 / 保存", Confirm), new("返回（放弃调整）", Back));
        var frame = ConsoleOverlayVisuals.SurfaceWithHints(panel, hints, "设置已被另一窗口修改，当前调整已保留。");
        var root = new Border { Child = frame, Background = ConsoleModeVisuals.Surface, Padding = new Thickness(24) };
        root.Measure(new Size(width, 650)); root.Arrange(new Rect(0, 0, width, 650)); root.UpdateLayout();
        var scroll = Assert.Single(Descendants<ScrollViewer>(frame));
        Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
        var before = hints.TransformToAncestor(root).TransformBounds(new Rect(hints.RenderSize));
        Assert.InRange(before.Bottom, 0, 650);
        Assert.InRange(before.Top, 0, 650);
        foreach (var bar in new[] { catalog, home, chord, hints })
        {
            foreach (FrameworkElement group in bar.Children)
            {
                var bounds = group.TransformToAncestor(bar).TransformBounds(new Rect(group.RenderSize));
                Assert.InRange(bounds.Right, 0, bar.ActualWidth + .01);
                Assert.InRange(bounds.Bottom, 0, bar.ActualHeight + .01);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(group)));
            }
        }
        Save(root, $"controller-prompts-{kind}-{width}-{scale}x", width, 650, scale);
        scroll.ScrollToEnd(); root.UpdateLayout();
        Assert.Equal(before, hints.TransformToAncestor(root).TransformBounds(new Rect(hints.RenderSize)));
    });

    private static void Save(Visual visual, string name, int width, int height, int scale)
    {
        if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VISUAL_OUTPUT") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(width * scale, height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static Task OnSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { test(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
