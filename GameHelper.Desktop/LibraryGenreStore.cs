using System.IO;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Desktop;

/// <summary>Per-catalog manual genre overrides. An empty override deliberately means unclassified.</summary>
internal sealed class LibraryGenreStore
{
    private readonly string? _path;
    private Dictionary<string, string[]> _manual;
    private readonly DesktopDocument<Dictionary<string, string[]>> _document;
    internal LibraryGenreStore(string? configPath)
    {
        _path = configPath is null ? null : configPath + ".desktop-genres.json";
        _document = new(_path, () => new());
        _manual = new(_document.Read(), StringComparer.OrdinalIgnoreCase);
    }
    internal bool TryRead(string key, out string[] genres) => _manual.TryGetValue(key, out genres!);
    internal void Save(string key, string[]? genres)
    {
        lock (_saveGate) SaveCore(key, genres);
    }
    private readonly object _saveGate = new();
    private void SaveCore(string key, string[]? genres)
    {
        if (_path is null) throw new InvalidOperationException("只读界面不能保存类型。");
        var next = new Dictionary<string, string[]>(_manual, StringComparer.OrdinalIgnoreCase);
        if (genres is null) next.Remove(key); else next[key] = SteamGenreClient.Normalize(genres);
        _document.Write(next);
        _manual = next;
    }
}

public sealed record GenreGroup(string Name, int Count);
