using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GameHelper.Desktop;
using GameHelper.Core.Models;

namespace GameHelper.Tests;

public sealed class ConsoleModeVisualTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task ShelfTextAndSelectionRemainReadableAndContentSized(string desktopTheme) => OnSta(() =>
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(SourcePath())!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameHelper.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var app = XDocument.Load(Path.Combine(directory!.FullName, "GameHelper.Desktop", "App.xaml"));
        var dictionary = app.Descendants().First(e => e.Name.LocalName == "ResourceDictionary");
        // Load the actual application styles without creating an Application or any native window.
        dictionary.Elements().First(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries").Remove();
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri($"/GameHelper.Desktop;component/Themes/{desktopTheme}.xaml", UriKind.Relative)));
        ConsoleModeWindow.ConfigureTheme(resources);
        var shelf = new ListBox();
        ConsoleModeWindow.ConfigureShelf(shelf);
        var surface = new Border { Background = new SolidColorBrush(Color.FromRgb(13, 23, 34)), Child = shelf, Resources = resources };
        surface.SetValue(TextElement.ForegroundProperty, Brushes.White);
        var selectedGame = new LibraryGameItem(new LibraryGame { Config = new() { DataKey = "selected", DisplayName = "🎮 Selected adventure" }, TotalMinutes = 228 });
        selectedGame.SetController(new ControllerSupport("partial"));
        var unselectedGame = new LibraryGameItem(new LibraryGame { Config = new() { DataKey = "unselected", DisplayName = "未选中的冒险" }, TotalMinutes = 162 });
        unselectedGame.SetController(new ControllerSupport("unlisted"));
        shelf.ItemsSource = new[] { selectedGame, unselectedGame };
        shelf.SelectedIndex = 0;
        surface.Measure(new Size(1280, 750)); surface.Arrange(new Rect(0, 0, 1280, 750)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 750, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VISUAL_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"console-shelf-{desktopTheme}.png")); encoder.Save(file);
        }
        var first = (ListBoxItem)shelf.ItemContainerGenerator.ContainerFromIndex(0);
        var second = (ListBoxItem)shelf.ItemContainerGenerator.ContainerFromIndex(1);
        Assert.Equal("🎮", Descendants<GamePosterTile>(first).Single().Label);
        Assert.Equal("未", Descendants<GamePosterTile>(second).Single().Label);
        var selectedBackground = ((SolidColorBrush)((Border)first.Template.FindName("CardChrome", first)).Background).Color;
        foreach (var label in Descendants<TextBlock>(first))
            Assert.True(Contrast(((SolidColorBrush)label.Foreground).Color, selectedBackground) >= 4.5);
        var labels = Descendants<TextBlock>(second).ToArray();
        Assert.NotEmpty(labels);
        foreach (var label in labels)
            Assert.True(Contrast(((SolidColorBrush)label.Foreground).Color, Color.FromRgb(13, 23, 34)) >= 4.5,
                $"{desktopTheme}: {label.Text} foreground {label.Foreground} is unreadable on console background");
        Assert.True(first.ActualHeight < 500, $"Selected card stretched to {first.ActualHeight} instead of its poster and text height");
        Assert.True(second.ActualHeight < 500, $"Unselected card stretched to {second.ActualHeight}");
        Assert.True(first.ActualHeight > 350);
        Assert.Contains(labels, label => label.Text == "不支持手柄" && ((SolidColorBrush)label.Foreground).Color == Colors.Salmon);
        // The console's local palette must not mutate the desktop's active palette.
        var desktopPalette = resources.MergedDictionaries[0];
        Assert.Equal(desktopTheme == "Light" ? Color.FromRgb(32, 32, 32) : Color.FromRgb(239, 237, 244),
            ((SolidColorBrush)desktopPalette["TextPrimary"]).Color);
    });

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
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
