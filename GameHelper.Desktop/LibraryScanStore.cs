using System.IO;

namespace GameHelper.Desktop;

/// <summary>
/// Desktop-local directory scan memory: recently scanned roots for one-click rescans and executables
/// the user ignored. Rescans still walk the whole directory, so newly installed games are never missed.
/// </summary>
internal sealed class LibraryScanStore
{
    internal const int MaxDirectories = 6;
    private readonly string? _path;
    private Document _document;
    private readonly DesktopDocument<Document> _storage;
    internal LibraryScanStore(string? configPath, IDesktopDocumentReader? reader = null)
    {
        _path = configPath is null ? null : configPath + ".desktop-scan.json";
        _storage = new(_path, () => new(), reader: reader);
        _document = _storage.Read();
    }
    internal IReadOnlyList<string> Directories => _document.Directories;
    internal IReadOnlyCollection<string> Ignored => _document.Ignored;

    /// <summary>Move a scanned root to the front of the recent list.</summary>
    internal void Remember(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(directory);
        Save(_document with
        {
            Directories = [normalized, .. _document.Directories.Where(item => !item.Equals(normalized, StringComparison.OrdinalIgnoreCase)).Take(MaxDirectories - 1)]
        });
    }

    internal void Forget(string directory) => Save(_document with
    {
        Directories = _document.Directories.Where(item => !item.Equals(Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)).ToList()
    });

    internal void SetIgnored(IReadOnlyList<string> executables, bool ignored)
    {
        var next = new HashSet<string>(_document.Ignored, StringComparer.OrdinalIgnoreCase);
        foreach (var executable in executables) { if (ignored) next.Add(executable); else next.Remove(executable); }
        Save(_document with { Ignored = next.Order(StringComparer.OrdinalIgnoreCase).ToList() });
    }

    // Read-only views (no config path) keep the memory in-process only.
    private void Save(Document next)
    {
        if (_path is not null)
        {
            _storage.Write(next);
        }
        _document = next;
    }

    private sealed record Document
    {
        public List<string> Directories { get; init; } = [];
        public List<string> Ignored { get; init; } = [];
    }
}
