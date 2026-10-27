using GameHelper.Core.Utilities;
using System.IO;
using System.Text.Json;

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
    internal LibraryScanStore(string? configPath)
    {
        _path = configPath is null ? null : configPath + ".desktop-scan.json";
        try
        {
            _document = _path is not null && File.Exists(_path)
                ? JsonSerializer.Deserialize<Document>(File.ReadAllText(_path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _document = new(); }
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
            AtomicFile.Write(_path, temporary =>
        {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            });
        }
        _document = next;
    }

    private sealed record Document
    {
        public List<string> Directories { get; init; } = [];
        public List<string> Ignored { get; init; } = [];
    }
}
