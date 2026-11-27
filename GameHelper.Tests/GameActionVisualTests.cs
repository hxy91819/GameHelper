using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class GameActionVisualTests
{
    [Fact]
    public async Task ActionTemplate_RendersManagedOptionsAndReflectsEligibility()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var root = Path.GetDirectoryName(SourcePath())!;
                var document = XDocument.Load(Path.Combine(root, "..", "GameHelper.Desktop", "MainWindow.xaml"));
                var ns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                var items = document.Descendants(ns + "ItemsControl").Single(x => (string?)x.Attribute("ItemsSource") == "{Binding ActionEditor.Items}");
                var template = new XElement(items.Descendants(ns + "DataTemplate").Single());
                template.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                foreach (var button in template.Descendants(ns + "Button")) button.Attribute("Click")?.Remove();
                var view = (FrameworkElement)((DataTemplate)XamlReader.Parse(template.ToString())).LoadContent();
                var action = new GameActionItem(new() { When = "beforeLaunch", Type = "run", Path = @"C:\Windows\System32\cmd.exe", SingleInstance = true, StopOnGameExit = true });
                view.DataContext = action;
                ((Border)view).Background = Brushes.White;
                view.Resources["Muted"] = Brushes.Gray;
                view.Measure(new Size(500, 700)); view.Arrange(new Rect(0, 0, 500, view.DesiredSize.Height)); view.UpdateLayout();
                var checks = Descendants(view).OfType<CheckBox>().ToArray();
                Assert.Equal(3, checks.Length);
                Assert.True(checks[1].IsChecked); Assert.True(checks[2].IsChecked);
                Assert.All(checks, c => Assert.True(c.IsEnabled));
                var image = new RenderTargetBitmap(500, (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(Path.GetTempPath(), "gh-actions-ui.png"))) encoder.Save(file);
                action.Wait = true;
                view.UpdateLayout();
                Assert.False(checks[1].IsEnabled); Assert.False(checks[2].IsEnabled);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static string SourcePath([CallerFilePath] string path = "") => path;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
}
