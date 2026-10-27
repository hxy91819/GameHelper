using System.IO;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Desktop;

/// <summary>Per-catalog manual genre overrides. An empty override deliberately means unclassified.</summary>
internal sealed class LibraryGenreStore
{
    private readonly string? _path;
    private readonly Dictionary<string, string[]> _manual;
    internal LibraryGenreStore(string? configPath)
    {
        _path = configPath is null ? null : configPath + ".desktop-genres.json";
        _manual = new Dictionary<string, string[]>(
            DesktopDocumentStore.Read(_path, () => new Dictionary<string, string[]>()), StringComparer.OrdinalIgnoreCase);
    }
    internal bool TryRead(string key, out string[] genres) => _manual.TryGetValue(key, out genres!);
    internal void Save(string key, string[]? genres)
    {
        if (_path is null) throw new InvalidOperationException("只读界面不能保存类型。");
        var next = new Dictionary<string, string[]>(_manual, StringComparer.OrdinalIgnoreCase);
        if (genres is null) next.Remove(key); else next[key] = SteamGenreClient.Normalize(genres);
        DesktopDocumentStore.Write(_path, next);
        _manual.Clear();
        foreach (var pair in next) _manual.Add(pair.Key, pair.Value);
    }
}

public sealed record GenreGroup(string Name, int Count);
