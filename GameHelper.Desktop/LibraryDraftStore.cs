using System.IO;
using System.Text.Json;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal sealed record LibraryDraft(LibraryGame Baseline, string Name, string Executable, string Target,
    string Arguments, string Directory, string Speed, bool Hdr, bool Enabled, bool UseSpeed,
    bool UseDs4Windows = false, string? Ds4WindowsPath = null, string SpeedHotkeys = "", List<GameAction>? Actions = null);

/// <summary>Unsaved UI drafts survive a forced update without becoming game configuration.</summary>
internal sealed class LibraryDraftStore(string configPath, IDesktopDocumentReader? reader = null)
{
    private readonly string _path = configPath + ".desktop-drafts.json";
    private string? _lastWritten;
    private readonly DesktopDocument<List<LibraryDraft>> _document = new(configPath + ".desktop-drafts.json", () => new(), recoverReadFailure: false, reader: reader);

    public IReadOnlyList<LibraryDraft> Read()
    {
        _lastWritten = null;
        // Keep the caller's read-failure status message instead of silently dropping drafts.
        return _document.Read();
    }

    internal bool UsesSqlite => _document.UsesSqlite;

    public void Write(IEnumerable<LibraryDraft> drafts)
    {
        var payload = JsonSerializer.Serialize(drafts);
        if (_lastWritten == payload || (!_document.UsesSqlite && payload == "[]" && !File.Exists(_path))) return;
        _document.Write(drafts.ToList());
        _lastWritten = payload;
    }
}
