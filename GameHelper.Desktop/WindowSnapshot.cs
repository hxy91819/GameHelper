using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameHelper.Desktop;

internal static class WindowSnapshot
{
    internal static void PrepareOffscreen(Window window)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR"))
            && Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VIEWPORT") is { } viewport
            && viewport.Split('x') is [var widthText, var heightText]
            && int.TryParse(widthText, out var width) && int.TryParse(heightText, out var height)
            && width is >= 960 and <= 2560 && height is >= 600 and <= 1600)
        {
            window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
            window.MinWidth = 960; window.MinHeight = 600;
            // WPF keeps a 2px non-client frame even for this offscreen borderless window.
            window.Width = width + 2; window.Height = height + 2;
        }
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000; window.Top = -32000;
    }

    internal static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var viewportProbe = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VIEWPORT"));
        var visual = viewportProbe && window.Content is FrameworkElement content ? content : (FrameworkElement)window;
        var pixelWidth = (int)visual.ActualWidth; var pixelHeight = (int)visual.ActualHeight;
        if (viewportProbe && Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VIEWPORT")!.Split('x') is [var requestedWidth, var requestedHeight]
            && int.TryParse(requestedWidth, out var width) && int.TryParse(requestedHeight, out var height))
        { pixelWidth = Math.Min(pixelWidth, width); pixelHeight = Math.Min(pixelHeight, height); }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
