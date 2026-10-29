using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class ConsoleOverlayVisualTests
{
    [Theory]
    [InlineData("Light", 960, 650)]
    [InlineData("Dark", 960, 650)]
    [InlineData("Light", 1920, 1080)]
    [InlineData("Dark", 1920, 1080)]
    public Task DialogRowsRemainReadableAndScrollable(string desktopTheme, int width, int height) => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri($"/GameHelper.Desktop;component/Themes/{desktopTheme}.xaml", UriKind.Relative)));
        var panel = new StackPanel();
        ConsoleOverlayVisuals.Header(panel, "游戏自动化设置", "很长的游戏名称 · 一段横跨星系的冒险，继续你尚未完成的旅程");
        string[] labels = ["自动 HDR", "启动 DS4Windows", "游戏倍速", "倍速倍率", "保存设置", "重新载入已保存配置", "取消，返回主机模式"];
        var buttons = new List<Button>();
        for (var i = 0; i < labels.Length; i++)
        {
            var row = ConsoleOverlayVisuals.Row(labels[i], i == 3 ? "左右调整 0.1× · 范围 0.1–10×" : "设置说明保持清晰，较长内容可在这一行内自然折行。",
                i < 3 ? "开启" : i == 3 ? "1.5×" : null, selected: i == 0, enabled: true, () => { }, primary: i == 4);
            panel.Children.Add(row); buttons.Add(row);
        }
        panel.Children.Add(ConsoleOverlayVisuals.Notice("DS4Windows 与倍速在下次启动游戏时应用；HDR 在后续自动调度时应用。"));
        panel.Children.Add(ConsoleModeVisuals.Label(@"DS4Windows 路径（在桌面游戏库修改）：C:\Tools\用于测试的非常长的游戏配套工具安装目录\DS4Windows\DS4Windows.exe", 15, ConsoleModeVisuals.Secondary));
        var frame = ConsoleOverlayVisuals.Surface(panel, "上下 选择 · 左右 调整 · A 切换 / 保存 · B 返回（放弃调整）", "设置已被另一窗口修改，请重新载入；当前调整已保留。");
        var root = new Border { Background = ConsoleModeVisuals.Surface, Padding = new Thickness(24), Resources = resources, Child = frame };
        Layout(root, width, height);
        var selected = buttons[0];
        Assert.Equal(ConsoleModeVisuals.Accent.Color, ((SolidColorBrush)selected.BorderBrush).Color);
        Assert.Equal(ConsoleOverlayVisuals.Selected.Color, ((SolidColorBrush)selected.Background).Color);
        Assert.All(buttons, button => Assert.InRange(button.ActualWidth, 1, width - 48));
        foreach (var label in Descendants<TextBlock>(root))
            Assert.True(Contrast(((SolidColorBrush)label.Foreground).Color, Background(label)) >= 4.5,
                $"{desktopTheme}: {label.Text} has insufficient contrast");
        var scroll = Assert.Single(Descendants<ScrollViewer>(frame));
        if (height == 650)
        {
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
            scroll.ScrollToEnd(); root.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            var last = buttons[^1].TransformToAncestor(scroll).TransformBounds(new Rect(buttons[^1].RenderSize));
            Assert.InRange(last.Bottom, 0, scroll.ViewportHeight);
            var hint = Assert.Single(Descendants<TextBlock>(frame), text => text.Text.StartsWith("上下 选择", StringComparison.Ordinal));
            var hintBounds = hint.TransformToAncestor(root).TransformBounds(new Rect(hint.RenderSize));
            Assert.InRange(hintBounds.Top, 0, height);
            Assert.InRange(hintBounds.Bottom, 0, height);
            var feedback = Assert.Single(Descendants<TextBlock>(frame), text => text.Text.StartsWith("设置已被另一窗口", StringComparison.Ordinal));
            var feedbackBounds = feedback.TransformToAncestor(root).TransformBounds(new Rect(feedback.RenderSize));
            Assert.InRange(feedbackBounds.Bottom, 0, height);
        }
        if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VISUAL_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, $"console-settings-{desktopTheme}-{width}x{height}.png")); encoder.Save(file);
        }
    });

    [Fact]
    public Task DisabledPrimaryAndKeyboardKeepReadableText() => OnSta(() =>
    {
        var panel = new StackPanel();
        var disabled = ConsoleOverlayVisuals.Row("保存设置", "正在保存，请稍候", null, true, false, () => { }, primary: true);
        var available = ConsoleOverlayVisuals.Row("可用操作", "立即执行", null, false, true, () => { });
        panel.Children.Add(disabled);
        panel.Children.Add(available);
        panel.Children.Add(ConsoleOverlayVisuals.Key("完成", true, true, () => { }));
        panel.Children.Add(ConsoleOverlayVisuals.Notice("确认结束游戏？未保存的进度可能丢失。", warning: true));
        var root = new Border { Background = ConsoleModeVisuals.Surface, Child = ConsoleOverlayVisuals.Surface(panel) };
        Layout(root, 960, 650);
        Assert.False(disabled.IsEnabled); Assert.Equal(1, disabled.Opacity);
        Assert.NotEqual(((SolidColorBrush)available.Background).Color, ((SolidColorBrush)disabled.Background).Color);
        var disabledTitle = Assert.Single(Descendants<TextBlock>(disabled), text => text.Text == "保存设置");
        var availableTitle = Assert.Single(Descendants<TextBlock>(available), text => text.Text == "可用操作");
        Assert.Equal(ConsoleModeVisuals.Secondary.Color, ((SolidColorBrush)disabledTitle.Foreground).Color);
        Assert.NotEqual(((SolidColorBrush)availableTitle.Foreground).Color, ((SolidColorBrush)disabledTitle.Foreground).Color);
        foreach (var label in Descendants<TextBlock>(root))
            Assert.True(Contrast(((SolidColorBrush)label.Foreground).Color, Background(label)) >= 4.5);
    });

    private static void Layout(FrameworkElement root, int width, int height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }

    private static Color Background(DependencyObject item)
    {
        for (var parent = VisualTreeHelper.GetParent(item); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Border { Background: SolidColorBrush border }) return border.Color;
            if (parent is Control { Background: SolidColorBrush control }) return control.Color;
        }
        return ConsoleModeVisuals.Surface.Color;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c) { static double S(byte v) { var x = v / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126 * S(c.R) + .7152 * S(c.G) + .0722 * S(c.B); }
        var x = L(a); var y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    private static Task OnSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { test(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
