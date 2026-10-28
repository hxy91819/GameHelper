using System.IO;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Desktop;

/// <summary>Per-catalog manual genre overrides. An empty override deliberately means unclassified.</summary>
internal sealed class LibraryGenreStore
{
    private readonly string? _path;
    private Dictionary<string, string[]> _manual;
    private readonly DesktopDocument<Dictionary<string, string[]>> _document;
    internal LibraryGenreStore(string? configPath, IDesktopDocumentReader? reader = null)
    {
        _path = configPath is null ? null : configPath + ".desktop-genres.json";
        _document = new(_path, () => new(), reader: reader);
        _manual = new(_document.Read(), StringComparer.OrdinalIgnoreCase);
    }
    internal bool TryRead(string key, out string[] genres) => _manual.TryGetValue(key, out genres!);
    internal void Save(string key, string[]? genres)
    {
        lock (_saveGate) SaveCore(key, genres);
    }
    // Order: _saveGate -> SQLite writer; release writer before taking the projection admission gate.
    // The worker releases admission before directory lease -> SQLite writer, and never takes _saveGate.
    // Never take this gate under a SQLite transaction, directory lease or projection gate. Storage/queue admission must not
    // synchronously call back into Save/this store; no UI dispatch, network, or user callbacks under the gate.
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
