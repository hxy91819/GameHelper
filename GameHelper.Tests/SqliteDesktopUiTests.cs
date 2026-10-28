using Moq;
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
        Assert.Equal("另一进程", (await JournalEditorViewModel.LoadAsync(local, "game", "游戏", new Clock())).Note);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CleanWindowReload_ReceivesOtherProcessDraftWithoutDeletingIt(bool inCatalog) => OnDispatcher(async () =>
    {
        await Seed();
        var baseline = new LibraryGame { Config = new() { DataKey = "game", Executable = "game.exe" }, Revision = "baseline", Installation = new(GameInstallationState.Installed, "installed") };
        var backend = new Moq.Mock<GameHelper.Core.Abstractions.IGameLibrary>();
        backend.Setup(b => b.ExecuteAsync(Moq.It.IsAny<GameLibraryRequest>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLibraryResponse { Success = true, Games = inCatalog ? [baseline] : [], ConfigPath = Config });
        using var model = new LibraryViewModel(backend.Object, Config);
        await model.RefreshAsync(); await model.FlushDraftsAsync();
        Assert.False(model.HasUnsavedChanges);
        var other = new LibraryDraftStore(Config); other.Read();
        var draft = new LibraryDraft(baseline, "另一进程草稿", "game.exe", "target", "--edit", "work", "2.", false, true, true, SpeedHotkeys: "Ctrl+F1=", Actions: []);
        other.Write([draft]);
        await model.RefreshAsync(discardDrafts: true);
        await model.FlushDraftsAsync();
        var preserved = Assert.Single(new LibraryDraftStore(Config).Read());
        Assert.Equal("另一进程草稿", preserved.Name); Assert.Equal("Ctrl+F1=", preserved.SpeedHotkeys);
        if (inCatalog) Assert.Equal("另一进程草稿", model.Selected!.Name);
        // A subsequent normal refresh must retain drafts whose game isn't currently in the catalog, too.
        await model.RefreshAsync(); await model.FlushDraftsAsync();
        Assert.Equal("另一进程草稿", Assert.Single(new LibraryDraftStore(Config).Read()).Name);
    });

    [Fact]
    public Task DiscardCurrentDraft_UsesOriginalExpectation_ConcurrentEditConflictsAndIsRetained() => OnDispatcher(async () =>
    {
        await Seed();
        var baseline = new LibraryGame { Config = new() { DataKey = "game", Executable = "game.exe" }, Revision = "baseline", Installation = new(GameInstallationState.Installed, "installed") };
        var backend = new Moq.Mock<GameHelper.Core.Abstractions.IGameLibrary>();
        backend.Setup(b => b.ExecuteAsync(Moq.It.IsAny<GameLibraryRequest>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLibraryResponse { Success = true, Games = [baseline], ConfigPath = Config });
        using var model = new LibraryViewModel(backend.Object, Config);
        await model.RefreshAsync();
        model.Selected!.Name = "本窗口编辑"; await model.FlushDraftsAsync();
        var other = new LibraryDraftStore(Config); var draft = Assert.Single(other.Read());
        other.Write([draft with { Name = "并发编辑" }]);
        await model.RefreshAsync(discardDrafts: true);
        Assert.Equal("本窗口编辑", model.Selected.Name);
        Assert.Contains("另一窗口", model.Status);
        Assert.Equal("并发编辑", Assert.Single(new LibraryDraftStore(Config).Read()).Name);
    });

    [Fact]
    public Task ReloadLatestDrafts_ClearsObsoleteCleanupFailure() => OnDispatcher(async () =>
    {
        await Seed();
        var baseline = new LibraryGame { Config = new() { DataKey = "game", Executable = "game.exe" }, Revision = "old", Installation = new(GameInstallationState.Installed, "installed") };
        var current = baseline;
        var backend = new Mock<GameHelper.Core.Abstractions.IGameLibrary>();
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GameLibraryResponse { Success = true, Games = [current], ConfigPath = Config });
        using var model = new LibraryViewModel(backend.Object, Config);
        await model.RefreshAsync(); model.Selected!.Name = "saved"; await model.FlushDraftsAsync();
        using var connection = Factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_cleanup BEFORE DELETE ON desktop_drafts BEGIN SELECT RAISE(ABORT,'cleanup failed'); END;";
        command.ExecuteNonQuery();
        current = baseline with { Config = new() { DataKey = "game", Executable = "game.exe", DisplayName = "saved" }, Revision = "new" };
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.IsBusy) && !model.IsBusy) saved.TrySetResult(); };
        model.SaveCommand.Execute(null); await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(model.HasUnsavedChanges);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.FlushDraftsAsync());
        command.CommandText = "DROP TRIGGER fail_cleanup;"; command.ExecuteNonQuery();
        await model.RefreshAsync(discardDrafts: true);
        await model.FlushDraftsAsync();
        Assert.Equal("saved", Assert.Single(new LibraryDraftStore(Config).Read()).Name);
    });

    [Fact]
    public async Task UnactivatedFallback_ArtworkKeepsLegacyCaseSensitiveEntries()
    {
        Environment.SetEnvironmentVariable("GAMEHELPER_DATA_DIR", _root);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        File.WriteAllText(Config + ".desktop-artwork.json", "{\"game\":123,\"GAME\":456}");
        using var storage = new SessionStorage(Factory, new Clock(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await storage.InitializeAsync(Config, []);
        Assert.False(storage.UsesSqlite);
        var artwork = new SteamPosterLibrary(Config, allowNetwork: false);
        Assert.Equal(123u, artwork.AssociatedAppId("game"));
        Assert.Equal(456u, artwork.AssociatedAppId("GAME"));
        Assert.Null(artwork.AssociatedAppId("Game"));
    }

    [Fact]
    public void UnactivatedFallback_OtherFiveDocumentsKeepLegacyReadRules()
    {
        Environment.SetEnvironmentVariable("GAMEHELPER_DATA_DIR", _root);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        File.WriteAllText(Config + ".desktop-journal.json", "{\"game\":{\"Status\":\"在玩\"},\"GAME\":{\"Status\":\"搁置\"}}");
        Assert.Equal("搁置", new LibraryJournalStore(Config).Read("game").Status);
        File.WriteAllText(Config + ".desktop-genres.json", "{\"game\":[]}");
        Assert.True(new LibraryGenreStore(Config).TryRead("GAME", out var genres)); Assert.Empty(genres);
        var directories = Enumerable.Range(0, 8).Select(i => "root-" + i).ToArray();
        File.WriteAllText(Config + ".desktop-scan.json", System.Text.Json.JsonSerializer.Serialize(new { Directories = directories, Ignored = new[] { "same.exe", "same.exe" } }));
        var scan = new LibraryScanStore(Config);
        Assert.Equal(directories, scan.Directories); Assert.Equal(2, scan.Ignored.Count);
        File.WriteAllText(Config + ".desktop-theme.json", "{\"Theme\":\"LIGHT\"}");
        Assert.False(new ThemePreferenceStore(Config).ReadLight());
        File.WriteAllText(Config + ".desktop-drafts.json", "null");
        Assert.Empty(new LibraryDraftStore(Config).Read());
    }

    [Fact]
    public Task StartupAndJournalOpen_ReadThroughReplaceableBoundaryOnWorkers() => OnDispatcher(async () =>
    {
        await Seed();
        var reader = new WorkerOnlyReader(Environment.CurrentManagedThreadId);
        var sideData = await DesktopSideData.LoadAsync(Config, reader: reader);
        Assert.Equal(6, reader.Paths.Count);
        using var model = new LibraryViewModel(Moq.Mock.Of<GameHelper.Core.Abstractions.IGameLibrary>(), Config, null, sideData: sideData);
        Assert.Equal(6, reader.Paths.Count); // The view constructor consumes preloaded data; no hidden connection/query.
        var editor = await JournalEditorViewModel.LoadAsync(sideData.Journal, "game", "游戏", new Clock());
        Assert.Equal(7, reader.Paths.Count);
        Assert.Equal("未开始", editor.Status);
    });

    private sealed class WorkerOnlyReader(int uiThreadId) : IDesktopDocumentReader
    {
        internal readonly System.Collections.Concurrent.ConcurrentBag<string?> Paths = [];
        public T Read<T>(string? path, Func<T> empty, bool recoverReadFailure,
            out SqliteDesktopStore? database, out DesktopDocumentSnapshot? baseline) where T : class
        {
            Assert.NotEqual(uiThreadId, Environment.CurrentManagedThreadId);
            Paths.Add(path);
            return DesktopDocumentReader.Instance.Read(path, empty, recoverReadFailure, out database, out baseline);
        }
    }
    private static Task OnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            }));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
