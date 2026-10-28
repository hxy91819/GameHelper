using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

// Frozen DTO contracts from the pre-SQLite Desktop stores. Do not apply YAML normalization.
internal sealed record LegacyJournal(string Status = "未开始", DateOnly? CompletedOn = null, string Note = "");
internal sealed record LegacyDraft(LibraryGame Baseline, string Name, string Executable, string Target,
    string Arguments, string Directory, string Speed, bool Hdr, bool Enabled, bool UseSpeed,
    bool UseDs4Windows = false, string? Ds4WindowsPath = null, string SpeedHotkeys = "", List<GameAction>? Actions = null);
internal sealed record LegacyScan
{
    public List<string> Directories { get; init; } = [];
    public List<string> Ignored { get; init; } = [];
}
internal sealed record LegacyTheme(string Theme);
internal sealed class LegacyGame
{
    public string GameName { get; set; } = "";
    public List<LegacySession>? Sessions { get; set; }
}
internal sealed class LegacySession
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long DurationMinutes { get; set; }
}
