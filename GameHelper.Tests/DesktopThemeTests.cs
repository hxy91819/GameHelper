using System.IO;
using System.Windows;
using System.Windows.Media;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class DesktopThemeTests
{
    [Fact]
    public void Preference_RoundTripsWithoutChangingConfigurationOrDrafts()
    {
        using var files = new Files();
        File.WriteAllText(files.Config, "games: []");
        File.WriteAllText(files.Config + ".desktop-drafts.json", "draft");
        var preference = new ThemePreferenceStore(files.Config);
        Assert.False(preference.ReadLight());
        Assert.False(File.Exists(files.Config + ".desktop-theme.json"));
        preference.Write(true);
        Assert.True(new ThemePreferenceStore(files.Config).ReadLight());
        preference.Write(false);
        Assert.False(new ThemePreferenceStore(files.Config).ReadLight());
        Assert.Equal("games: []", File.ReadAllText(files.Config));
        Assert.Equal("draft", File.ReadAllText(files.Config + ".desktop-drafts.json"));
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Theme\":\"unknown\"}")]
    [InlineData("null")]
    public void Preference_InvalidContentFallsBackToDark(string content)
    {
        using var files = new Files();
        File.WriteAllText(files.Config + ".desktop-theme.json", content);
        Assert.False(new ThemePreferenceStore(files.Config).ReadLight());
    }

    [Fact]
    public Task Palettes_HaveMatchingResourcesAndReadableText() => OnSta(() =>
    {
        var dark = Palette("Dark"); var light = Palette("Light");
        Assert.Equal(dark.Keys.Cast<string>().Order(), light.Keys.Cast<string>().Order());
        foreach (var palette in new[] { dark, light })
            foreach (var (foreground, background) in new[] { ("TextPrimary", "Page"), ("Muted", "Page"),
                ("TextPrimary", "Surface"), ("TextPrimary", "SelectedSurface"), ("Accent", "Page"), ("AccentText", "Accent"), ("Warning", "Surface"), ("Success", "Page") })
            {
                var a = Luminance(((SolidColorBrush)palette[foreground]).Color);
                var b = Luminance(((SolidColorBrush)palette[background]).Color);
                Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5, $"{foreground} on {background}");
            }
    });

    [Fact]
    public Task Theme_RestartRestoresSavedPaletteAndSwitchesBack() => OnSta(() =>
    {
        using var files = new Files();
        var resources = new ResourceDictionary();
        var theme = new DesktopTheme(resources, new ThemePreferenceStore(files.Config));
        theme.Load();
        var dark = ((SolidColorBrush)resources["Page"]).Color;
        theme.Set(true, persist: true);
        var restarted = new DesktopTheme(resources, new ThemePreferenceStore(files.Config));
        restarted.Load();
        Assert.True(restarted.IsLight);
        Assert.NotEqual(dark, ((SolidColorBrush)resources["Page"]).Color);
        restarted.Set(false, persist: true);
        Assert.False(new ThemePreferenceStore(files.Config).ReadLight());
        Assert.Equal(dark, ((SolidColorBrush)resources["Page"]).Color);
        Assert.Single(resources.MergedDictionaries);
    });

    [Fact]
    public Task Theme_FailedPreferenceSaveDoesNotPartiallyChangePalette() => OnSta(() =>
    {
        using var files = new Files();
        var resources = new ResourceDictionary();
        var theme = new DesktopTheme(resources, new ThemePreferenceStore(files.Config));
        theme.Load();
        var dark = ((SolidColorBrush)resources["Page"]).Color;
        theme.Set(true, persist: false);
        Assert.NotEqual(dark, ((SolidColorBrush)resources["Page"]).Color);
        Assert.False(File.Exists(files.Config + ".desktop-theme.json"));
        theme.Set(false, persist: false);
        Directory.CreateDirectory(files.Config + ".desktop-theme.json");
        Assert.Throws<UnauthorizedAccessException>(() => theme.Set(true, persist: true));
        Assert.False(theme.IsLight);
        Assert.Equal(dark, ((SolidColorBrush)resources["Page"]).Color);
        Assert.Single(resources.MergedDictionaries);
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    });

    private static ResourceDictionary Palette(string name) => (ResourceDictionary)Application.LoadComponent(
        new Uri($"/GameHelper.Desktop;component/Themes/{name}.xaml", UriKind.Relative));

    private static double Luminance(Color color)
    {
        static double Linear(byte value) { var channel = value / 255d; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static Task OnSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { test(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "GameHelper.Theme." + Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(Root, "config.yml");
        internal Files() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
