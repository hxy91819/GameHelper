using GameHelper.Core.Models;
using GameHelper.Desktop;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;

namespace GameHelper.Tests;

public sealed class SqliteDesktopUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GH.DesktopUi." + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR");
    private string Config => Path.Combine(_root, "GameHelper", "config.yml");
    private SqliteConnectionFactory Factory => new(Path.Combine(_root, "GameHelper"));
    private async Task Seed()
    {
        Environment.SetEnvironmentVariable("GAMEHELPER_DATA_DIR", _root);
        var migration = new SqliteLegacyMigration(Factory, TimeProvider.System);
        var candidate = await migration.PrepareAsync([Config], sessionsOnly: true);
        await migration.ActivateAsync(candidate.ManifestPath);
        await SqliteDesktopStore.PrepareAsync(Factory, Config);
    }

    [Fact]
    public async Task JournalConflict_KeepsEditorFieldsAndSavedValue_ReopeningReloads()
    {
        await Seed();
        var local = new LibraryJournalStore(Config);
        var editor = new JournalEditorViewModel(local, "game", "游戏", new Clock()) { Status = "已通关", Note = "保留编辑" };
        new LibraryJournalStore(Config).Save("game", new("搁置", null, "另一进程"));
        Assert.False(await editor.SaveAsync());
        Assert.Equal("保留编辑", editor.Note);
        Assert.Equal("已通关", editor.Status);
        Assert.Equal(new DateTime(2025, 1, 3), editor.CompletedOn);
        Assert.Contains("另一窗口", editor.Error);
        Assert.Equal("另一进程", new LibraryJournalStore(Config).Read("game").Note);
        Assert.Equal("另一进程", new JournalEditorViewModel(local, "game", "游戏", new Clock()).Note);
    }

    [Fact]
    public async Task DraftSaveFailure_RetainsAllInputAndDoesNotAdvanceDeduplication()
    {
        await Seed();
        var store = new LibraryDraftStore(Config);
        store.Read();
        var draft = new LibraryDraft(new LibraryGame { Config = new() { DataKey = "game", Executable = "game.exe" } },
            "未完成", "game.exe", "", "--new", "dir", "2.", false, true, true, SpeedHotkeys: "Ctrl+F1=");
        var other = new LibraryDraftStore(Config); other.Read(); other.Write([draft with { Name = "另一进程" }]);
        Assert.Throws<InvalidOperationException>(() => store.Write([draft]));
        Assert.Equal("未完成", draft.Name); Assert.Equal("Ctrl+F1=", draft.SpeedHotkeys); Assert.Equal("2.", draft.Speed);
        Assert.Equal("另一进程", Assert.Single(new LibraryDraftStore(Config).Read()).Name);
        store.Read(); // Explicit reload obtains a new expectation; failed payload wasn't marked as written.
        store.Write([draft]);
        Assert.Equal("未完成", Assert.Single(new LibraryDraftStore(Config).Read()).Name);
    }

    [Fact]
    public async Task ThemeAndScanAndGenreAndArtwork_UseDatabaseAfterScopeActivation()
    {
        await Seed();
        var theme = new ThemePreferenceStore(Config); Assert.False(theme.ReadLight()); theme.Write(true);
        var scan = new LibraryScanStore(Config);
        foreach (var item in Enumerable.Range(0, 8)) scan.Remember("root-" + item);
        scan.SetIgnored(["É.exe"], true);
        new LibraryGenreStore(Config).Save("game", []);
        var artwork = new SteamPosterLibrary(Config, allowNetwork: false);
        artwork.Associate("game", 123);
        Assert.Equal(123u, new SteamPosterLibrary(Config, allowNetwork: false).AssociatedAppId("GAME"));
        var reopened = new LibraryScanStore(Config);
        Assert.Equal(new[] { "root-7", "root-6", "root-5", "root-4", "root-3", "root-2" }, reopened.Directories);
        Assert.Contains("É.exe", reopened.Ignored);
        Assert.True(new ThemePreferenceStore(Config).ReadLight());
        Assert.True(new LibraryGenreStore(Config).TryRead("GAME", out var genres)); Assert.Empty(genres);
    }

    [Fact]
    public async Task DatabaseWriteFailure_KeepsJournalInput_ThenRetriesSameEdit()
    {
        await Seed();
        using var connection = Factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_journal BEFORE INSERT ON journals BEGIN SELECT RAISE(ABORT,'test failure'); END;";
        command.ExecuteNonQuery();
        var editor = new JournalEditorViewModel(new LibraryJournalStore(Config), "game", "游戏", new Clock()) { Note = "不能丢失" };
        Assert.False(await editor.SaveAsync());
        Assert.Equal("不能丢失", editor.Note); Assert.Contains("未保存", editor.Error);
        Assert.Equal("", new LibraryJournalStore(Config).Read("game").Note);
        command.CommandText = "DROP TRIGGER fail_journal;"; command.ExecuteNonQuery();
        Assert.True(await editor.SaveAsync());
        Assert.Equal("不能丢失", new LibraryJournalStore(Config).Read("game").Note);
    }

    public void Dispose()
    {
        DesktopDocumentStore.DrainProjectionsAsync().GetAwaiter().GetResult();
        Environment.SetEnvironmentVariable("GAMEHELPER_DATA_DIR", _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddYears(55).AddDays(2);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
