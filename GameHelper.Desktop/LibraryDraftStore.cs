using System.IO;
using System.Text.Json;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal sealed record LibraryDraft(LibraryGame Baseline, string Name, string Executable, string Target,
    string Arguments, string Directory, string Speed, bool Hdr, bool Enabled, bool UseSpeed,
    bool UseDs4Windows = false, string? Ds4WindowsPath = null, string SpeedHotkeys = "", List<GameAction>? Actions = null);

/// <summary>Unsaved UI drafts survive a forced update without becoming game configuration.</summary>
internal sealed class LibraryDraftStore(string configPath)
{
    private readonly string _path = configPath + ".desktop-drafts.json";
    private string? _lastWritten;

    public IReadOnlyList<LibraryDraft> Read()
    {
        // Keep the caller's read-failure status message instead of silently dropping drafts.
        return DesktopDocumentStore.Read(_path, () => new List<LibraryDraft>(), recoverReadFailure: false);
    }

    public void Write(IEnumerable<LibraryDraft> drafts)
    {
        var payload = JsonSerializer.Serialize(drafts);
        if (_lastWritten == payload || (payload == "[]" && !File.Exists(_path))) return;
        DesktopDocumentStore.WriteJson(_path, () => payload, resolveDirectory: false);
        _lastWritten = payload;
    }
}
