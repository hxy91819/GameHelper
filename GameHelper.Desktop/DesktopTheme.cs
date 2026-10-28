using System.IO;
using System.Windows;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Per-configuration appearance preference, independent of runtime configuration and drafts.</summary>
internal sealed class ThemePreferenceStore(string configPath)
{
    private readonly DesktopDocument<Preference> _document = new(configPath + ".desktop-theme.json", () => new("dark"));

    internal bool ReadLight()
    {
        return _document.Read().Theme == "light";
    }

    internal void Write(bool light)
    {
        _document.Write(new Preference(light ? "light" : "dark"));
    }

    private sealed record Preference(string Theme);
}

/// <summary>Swaps application palette resources; inherited revision invalidates custom-drawn charts too.</summary>
internal sealed class DesktopTheme(ResourceDictionary resources, ThemePreferenceStore preferences)
{
    private static readonly DependencyProperty RevisionProperty = DependencyProperty.RegisterAttached("Revision", typeof(int),
        typeof(DesktopTheme), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    private int _revision;
    internal bool IsLight { get; private set; }

    internal void Load() => Set(preferences.ReadLight(), persist: false);

    internal async Task SetAsync(bool light)
    {
        await Task.Run(() => preferences.Write(light));
        Set(light, persist: false);
    }

    internal void Set(bool light, bool persist)
    {
        var palette = (ResourceDictionary)Application.LoadComponent(new Uri($"/GameHelper.Desktop;component/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative));
        if (persist) preferences.Write(light);
        var existing = resources.MergedDictionaries.FirstOrDefault(dictionary => dictionary.Contains("Page"));
        if (existing is null) resources.MergedDictionaries.Add(palette);
        else resources.MergedDictionaries[resources.MergedDictionaries.IndexOf(existing)] = palette;
        IsLight = light;
        if (Application.Current is { } application)
            foreach (Window window in application.Windows) window.SetValue(RevisionProperty, ++_revision);
    }
}
