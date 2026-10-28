using System.IO;
using System.Text.Json;

namespace GameHelper.Desktop;

/// <summary>Worker-loaded side stores; constructing WPF views consumes memory only.</summary>
internal sealed record DesktopSideData(LibraryGenreStore Genres, LibraryScanStore Scan, LibraryJournalStore Journal,
    SteamPosterLibrary Posters, LibraryDraftStore? DraftStore, IReadOnlyList<LibraryDraft> Drafts,
    ThemePreferenceStore ThemePreferences, bool IsLight, string? DraftError)
{
    internal static Task<DesktopSideData> LoadAsync(string configPath, bool readOnly = false, IDesktopDocumentReader? reader = null) => Task.Run(() =>
    {
        var genres = new LibraryGenreStore(configPath, reader);
        var scan = new LibraryScanStore(readOnly ? null : configPath, reader);
        var journal = new LibraryJournalStore(configPath, reader);
        var posters = new SteamPosterLibrary(configPath, allowNetwork: !readOnly, reader: reader);
        var drafts = readOnly ? null : new LibraryDraftStore(configPath, reader);
        IReadOnlyList<LibraryDraft> restored = [];
        string? error = null;
        try { if (drafts is not null) restored = drafts.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { error = "上次未保存的修改未能读取，已忽略。"; }
        var theme = new ThemePreferenceStore(configPath, reader);
        return new DesktopSideData(genres, scan, journal, posters, drafts, restored, theme, theme.ReadLight(), error);
    });
}
