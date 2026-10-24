using System.IO;
using System.Text.Json;
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
        try
        {
            _manual = new Dictionary<string, string[]>(_path is not null && File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_path)) ?? [] : [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _manual = new(StringComparer.OrdinalIgnoreCase); }
    }
    internal bool TryRead(string key, out string[] genres) => _manual.TryGetValue(key, out genres!);
    internal void Save(string key, string[]? genres)
    {
        if (_path is null) throw new InvalidOperationException("只读界面不能保存类型。");
        var next = new Dictionary<string, string[]>(_manual, StringComparer.OrdinalIgnoreCase);
        if (genres is null) next.Remove(key); else next[key] = SteamGenreClient.Normalize(genres);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, _path, true);
            _manual.Clear();
            foreach (var pair in next) _manual.Add(pair.Key, pair.Value);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record GenreGroup(string Name, int Count);
