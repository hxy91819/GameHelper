using System.IO;
using System.Text.Json;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal sealed record LibraryDraft(LibraryGame Baseline, string Name, string Executable, string Target,
    string Arguments, string Directory, string Speed, bool Hdr, bool Enabled, bool UseSpeed);

/// <summary>Unsaved UI drafts survive a forced update without becoming game configuration.</summary>
internal sealed class LibraryDraftStore(string configPath)
{
    private readonly string _path = configPath + ".desktop-drafts.json";
    private string? _lastWritten;

    public IReadOnlyList<LibraryDraft> Read()
    {
        if (!File.Exists(_path)) return [];
        return JsonSerializer.Deserialize<List<LibraryDraft>>(File.ReadAllText(_path)) ?? [];
    }

    public void Write(IEnumerable<LibraryDraft> drafts)
    {
        var payload = JsonSerializer.Serialize(drafts);
        if (_lastWritten == payload || (payload == "[]" && !File.Exists(_path))) return;
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, payload);
            File.Move(temp, _path, true);
            _lastWritten = payload;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
