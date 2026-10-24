using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Per-configuration appearance preference, independent of runtime configuration and drafts.</summary>
internal sealed class ThemePreferenceStore(string configPath)
{
    private readonly string _path = configPath + ".desktop-theme.json";

    internal bool ReadLight()
    {
        try { return File.Exists(_path) && JsonSerializer.Deserialize<Preference>(File.ReadAllText(_path))?.Theme == "light"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal void Write(bool light)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preference(light ? "light" : "dark")));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
