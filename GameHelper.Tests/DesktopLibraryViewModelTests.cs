using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.Desktop;
using Moq;

namespace GameHelper.Tests;

public sealed class DesktopLibraryViewModelTests
{
    [Fact]
    public Task SaveChangedSteamTarget_RefreshesAutomaticGenres() => OnDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-genre-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new System.Net.Http.HttpClient(new GenreIdentityHandler());
            var config = Path.Combine(root, "config.yml");
            var artwork = new GameHelper.Infrastructure.Artwork.SteamArtworkClient(http, Path.Combine(root, "posters"), _ => false);
            var posters = new SteamPosterLibrary(config, true, artwork, _ => null);
            var clock = new FixedClock(DateTimeOffset.UnixEpoch);
            var genres = new GameHelper.Infrastructure.Artwork.SteamGenreClient(http, Path.Combine(root, "genres"), clock);
            var backend = new Backend(); backend.Game.Launch = new GameLaunchOptions { Target = "steam://rungameid/123" };
            using var model = new LibraryViewModel(backend, config, posters, genreClient: genres);
            await model.RefreshAsync(); await model.Selected!.GenresLoading;
            Assert.Equal("动作", model.Selected.GenreLabel);
            clock.Advance(TimeSpan.FromSeconds(2));
            model.Selected.Target = "steam://rungameid/456"; model.SaveCommand.Execute(null);
            await model.Selected!.GenresLoading;
            Assert.Equal("策略", model.Selected.GenreLabel);
        }
        finally { Directory.Delete(root, true); }
    });

    private sealed class GenreIdentityHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("appdetails", StringComparison.Ordinal)
                ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound)
            { Content = new System.Net.Http.StringContent("""{"123":{"success":true,"data":{"genres":[{"description":"动作"}]}},"456":{"success":true,"data":{"genres":[{"description":"策略"}]}}}""") });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SaveDuringGenreRequest_TransfersResultWithoutOverwritingManualChoice(bool manual) => OnDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-pending-genres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var handler = new DeferredGenreHandler();
            using var http = new System.Net.Http.HttpClient(handler);
            var client = new GameHelper.Infrastructure.Artwork.SteamArtworkClient(http, Path.Combine(root, "posters"), _ => false);
            var config = Path.Combine(root, "config.yml");
            var posters = new SteamPosterLibrary(config, true, client, _ => null);
            var genres = new GameHelper.Infrastructure.Artwork.SteamGenreClient(http, Path.Combine(root, "genres"));
            var backend = new Backend(); backend.Game.Launch = new GameLaunchOptions { Target = "steam://rungameid/123" };
            using var model = new LibraryViewModel(backend, config, posters, genreClient: genres);
            await model.RefreshAsync(); await handler.Started.Task;
            var original = model.Selected!;
            original.Hdr = true; model.SaveCommand.Execute(null);
            var replacement = model.Selected!;
            Assert.NotSame(original, replacement);
            if (manual) await model.SaveGenresAsync(replacement, ["策略"]);
            handler.Response.SetResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("""{"123":{"success":true,"data":{"genres":[{"description":"动作"}]}}}""") });
            await original.GenresLoading; await replacement.GenresLoading;
            // Yield to the carried result when manual loading replaced the public task.
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.Equal(manual ? "策略" : "动作", replacement.GenreLabel);
            Assert.Equal(1, backend.Saves);
        }
        finally { Directory.Delete(root, true); }
    });

    private sealed class DeferredGenreHandler : System.Net.Http.HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<System.Net.Http.HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("appdetails", StringComparison.Ordinal))
            { Started.TrySetResult(); return Response.Task.WaitAsync(cancellationToken); }
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public Task LaunchAfterSavingFilteredGame_KeepsOriginalTargetAndKnownGenres() => OnSta(() =>
    {
        var backend = new Backend(); backend.Game.Launch = new GameLaunchOptions { Target = "launch.exe" };
        using var model = new LibraryViewModel(backend);
        model.RefreshAsync().GetAwaiter().GetResult();
        model.Selected!.GenresLoading.GetAwaiter().GetResult();
        model.Selected.SetGenres(["动作", "角色扮演"]);
        model.SetGenre("角色扮演");
        model.Selected!.Hdr = true;
        model.LaunchCommand.Execute(null);
        Assert.Equal(1, backend.Saves);
        Assert.True(backend.Launches == 1, model.Status);
        Assert.Equal("a", model.Selected!.DataKey);
        Assert.Contains("角色扮演", model.Selected.Genres);
    });

    [Fact]
    public Task GenreFilters_SupportMultipleMembershipAndKeepDrafts() => OnSta(() =>
    {
        using var model = new LibraryViewModel(new Backend());
        model.RefreshAsync().GetAwaiter().GetResult();
        var game = model.Selected!; game.GenresLoading.GetAwaiter().GetResult(); game.Name = "draft";
        game.SetGenres(["动作", "角色扮演"]);
        Assert.Equal(3, model.GenreGroups.Count);
        model.SetGenre("角色扮演"); Assert.Same(game, model.Selected);
        model.SetGenre("未分类"); Assert.True(model.Games.IsEmpty);
        model.SetGenre(null); Assert.Same(game, model.Selected); Assert.True(game.IsDirty);
        game.SetGenres([]); model.SetGenre("未分类"); Assert.Same(game, model.Selected);
    });

    [Fact]
    public Task PosterRefresh_WhenOfflineRetainsSteamClientCache() => OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-poster-offline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var local = Path.Combine(root, "steam-client.jpg");
            var frame = System.Windows.Media.Imaging.BitmapSource.Create(200, 300, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[200 * 300 * 4], 200 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
            using (var stream = File.Create(local)) encoder.Save(stream);
            using var handler = new OfflinePosterHandler();
            using var http = new System.Net.Http.HttpClient(handler);
            var client = new GameHelper.Infrastructure.Artwork.SteamArtworkClient(http, Path.Combine(root, "cache"),
                GameHelper.Desktop.LocalGameArtwork.IsValidPoster);
            var library = new GameHelper.Desktop.SteamPosterLibrary(null, true, client, _ => local);
            var artwork = library.LoadAsync("a", "unknown.exe", "steam://rungameid/620", CancellationToken.None, refresh: true).GetAwaiter().GetResult();
            Assert.True(artwork.IsCover);
            Assert.NotNull(artwork.Image);
            Assert.True(artwork.Image!.IsFrozen);
            Assert.False(File.Exists(Path.Combine(root, "cache", "620.jpg")));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public Task StatisticsArtwork_ReusesLoadedPosterByDataKey() => OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-stats-art", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var poster = Path.Combine(root, "poster.png");
            var frame = System.Windows.Media.Imaging.BitmapSource.Create(200, 300, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[200 * 300 * 4], 200 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
            using (var stream = File.Create(poster)) encoder.Save(stream);
            var config = Path.Combine(root, "config.yml");
            using var http = new System.Net.Http.HttpClient(new OfflinePosterHandler());
            var client = new GameHelper.Infrastructure.Artwork.SteamArtworkClient(http, Path.Combine(root, "cache"), GameHelper.Desktop.LocalGameArtwork.IsValidPoster);
            var posters = new GameHelper.Desktop.SteamPosterLibrary(config, allowNetwork: false, client, _ => poster);
            posters.Associate("a", 620);
            using var model = new LibraryViewModel(new Backend(), config, posters);
            model.RefreshAsync().GetAwaiter().GetResult();
            var game = model.Games.Cast<LibraryGameItem>().Single();
            game.ArtworkLoading.GetAwaiter().GetResult();
            Assert.True(game.ArtworkIsCover);
            model.Statistics.RefreshAsync().GetAwaiter().GetResult();
            var row = Assert.IsType<StatisticsGameRow>(model.Statistics.Games.Cast<object>().Single());
            Assert.Same(game.Artwork, row.Artwork);
            Assert.Same(game.Artwork, model.Statistics.FavouriteArtwork);
        }
        finally { Directory.Delete(root, true); }
    });

    private sealed class OfflinePosterHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    [Fact]
    public Task NameSearch_SavesOnlyUniqueExactMatchesAndNeverOverridesExistingAssociation() => OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-poster-name", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "config.yml"); File.WriteAllText(config, "unchanged");
            using var http = new System.Net.Http.HttpClient(new NameSearchHandler());
            var client = new GameHelper.Infrastructure.Artwork.SteamArtworkClient(http, Path.Combine(root, "cache"), _ => false);
            var posters = new SteamPosterLibrary(config, true, client, _ => null);
            Assert.True(posters.MatchNameAsync("portal", "Portal 2™", CancellationToken.None).GetAwaiter().GetResult());
            Assert.Contains("620", File.ReadAllText(config + ".desktop-artwork.json"));
            Assert.False(posters.MatchNameAsync("portal", "Game", CancellationToken.None).GetAwaiter().GetResult());
            Assert.False(posters.MatchNameAsync("ambiguous", "Game", CancellationToken.None).GetAwaiter().GetResult());
            Assert.False(posters.MatchNameAsync("wrong-edition", "Portal 2 Deluxe", CancellationToken.None).GetAwaiter().GetResult());
            Assert.False(posters.HasAssociation("ambiguous")); Assert.False(posters.HasAssociation("wrong-edition"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => posters.MatchNameAsync("cancelled", "Portal 2", cancelled.Token).GetAwaiter().GetResult());
            Assert.False(posters.HasAssociation("cancelled")); Assert.Equal("unchanged", File.ReadAllText(config));
        }
        finally { Directory.Delete(root, true); }
    });

    private sealed class NameSearchHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("""{"items":[{"id":620,"name":"Portal 2","type":"app"},{"id":1,"name":"Game","type":"app"},{"id":2,"name":"Game","type":"app"}]}""") });
    }
    [Fact]
    public Task PosterAssociation_PersistsSeparatelyWithoutChangingLaunchOrAutomation() => OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-poster-links", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config.yml");
        Directory.CreateDirectory(root);
        File.WriteAllText(config, "original config");
        try
        {
            var posters = new GameHelper.Desktop.SteamPosterLibrary(config, allowNetwork: false);
            posters.Associate("a", 620);
            var payload = File.ReadAllText(config + ".desktop-artwork.json");
            Assert.Contains("620", payload);
            Assert.Equal("original config", File.ReadAllText(config));
            var reopened = new GameHelper.Desktop.SteamPosterLibrary(config, allowNetwork: false);
            reopened.Associate("b", 621);
            Assert.Contains("620", File.ReadAllText(config + ".desktop-artwork.json"));
            reopened.Associate("a", null);
            Assert.DoesNotContain("620", File.ReadAllText(config + ".desktop-artwork.json"));
            Assert.Contains("621", File.ReadAllText(config + ".desktop-artwork.json"));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    });
    [Fact]
    public Task InstallationGroups_DefaultToInstalledAndPreserveDraftWhenFileDisappears() => OnSta(() =>
    {
        var backend = new Backend();
        using var model = new LibraryViewModel(backend);
        model.RefreshAsync().GetAwaiter().GetResult();
        Assert.Equal("installed", model.InstallationGroup);
        var original = model.Selected!; original.Name = "draft";
        backend.Installation = GameInstallationState.Missing;
        model.RefreshAsync().GetAwaiter().GetResult();
        Assert.True(model.Games.IsEmpty); Assert.Null(model.Selected);
        model.SetInstallationGroup("missing");
        Assert.Same(original, model.Selected); Assert.Equal("draft", model.Selected!.Name);
        Assert.True(model.Selected.IsDirty);
        model.SetInstallationGroup("unknown"); Assert.True(model.Games.IsEmpty);
        model.SetInstallationGroup("all"); Assert.Same(original, model.Selected);
        backend.Installation = GameInstallationState.Installed;
        model.RefreshAsync().GetAwaiter().GetResult();
        model.SetInstallationGroup("installed"); Assert.Same(original, model.Selected);
        Assert.Equal(0, backend.Saves); Assert.Equal(0, backend.Launches);
    });

    [Fact]
    public Task ClearingEmptySearch_RestoresSelectionAndItsDraft() => OnSta(() =>
    {
        using var model = new LibraryViewModel(new Backend());
        model.RefreshAsync().GetAwaiter().GetResult();
        var original = model.Selected!;
        original.Name = "Unfinished draft";
        model.Search = "__no_match__";
        Assert.Null(model.Selected);
        model.Search = string.Empty;
        Assert.Same(original, model.Selected);
        Assert.True(model.Selected!.IsDirty);
    });
    [Fact]
    public Task Polling_PreservesDraftAndSelection_WhileUpdatingRuntimeState() => OnSta(() =>
    {
        var backend = new Backend();
        using var model = new LibraryViewModel(backend);
        model.RefreshAsync().GetAwaiter().GetResult();
        var selected = model.Selected!;
        selected.Name = "My draft";
        backend.State = "运行中";
        model.RefreshAsync().GetAwaiter().GetResult();
        Assert.Same(selected, model.Selected);
        Assert.Equal("My draft", model.Selected!.Name);
        Assert.Equal("运行中", model.Selected.State);
        Assert.True(model.Selected.IsDirty);
    });

    [Fact]
    public Task FailedSave_DoesNotLaunchWithUnsavedSettings() => OnSta(() =>
    {
        var backend = new Backend { RejectSave = true };
        using var model = new LibraryViewModel(backend);
        model.RefreshAsync().GetAwaiter().GetResult();
        model.Selected!.Hdr = true;
        model.LaunchCommand.Execute(null);
        Assert.Equal(1, backend.Saves);
        Assert.Equal(0, backend.Launches);
        Assert.True(model.Selected.IsDirty);
        Assert.Contains("conflict", model.Status);
        Assert.Equal(model.Status, model.ActionError);
        Assert.True(model.HasActionError);
    });

    [Fact]
    public Task ReloadConflictingGamePreservesOtherDraftsAndAllowsLaunch() => OnSta(() =>
    {
        var current = new GameConfig { DataKey = "a", Executable = @"C:\a.exe", DisplayName = "A", Launch = new() { Target = "steam://rungameid/620" } };
        var other = new GameConfig { DataKey = "b", Executable = @"C:\b.exe", DisplayName = "B" };
        var launches = 0;
        var backend = new Moq.Mock<IGameLibrary>();
        backend.Setup(value => value.ExecuteAsync(Moq.It.IsAny<GameLibraryRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns<GameLibraryRequest, CancellationToken>((request, _) =>
            {
                if (request.Operation == GameLibraryOperation.Save)
                {
                    if (request.ExpectedRevision != GameConfigRevision.For(current))
                        return Task.FromResult(new GameLibraryResponse { Error = "conflict" });
                    current = request.Game!;
                }
                if (request.Operation == GameLibraryOperation.Launch) { launches++; Assert.Equal(1.5, current.SpeedMultiplier); }
                return Task.FromResult(new GameLibraryResponse { Success = true, MonitorRunning = true,
                    Games = new[] { current, other }.Select(game => new LibraryGame { Config = game,
                        Revision = GameConfigRevision.For(game), Installation = new(GameInstallationState.Installed, "test") }).ToList() });
            });
        using var model = new LibraryViewModel(backend.Object);
        model.RefreshAsync().GetAwaiter().GetResult();
        var otherDraft = model.Games.Cast<LibraryGameItem>().Single(game => game.DataKey == "b");
        otherDraft.Name = "keep my draft";
        model.Selected = model.Games.Cast<LibraryGameItem>().Single(game => game.DataKey == "a");
        model.Selected.UseSpeed = true; model.Selected.Speed = "2";
        current.SpeedMultiplier = 1.5;
        model.LaunchCommand.Execute(null);
        Assert.Equal(0, launches); Assert.True(model.HasActionError); Assert.True(model.Selected.IsDirty);
        model.ReloadSelectedCommand.Execute(null);
        Assert.False(model.HasActionError); Assert.False(model.Selected!.IsDirty);
        Assert.Equal("a", model.Selected.DataKey); Assert.Equal("1.5", model.Selected.Speed);
        Assert.Same(otherDraft, model.Games.Cast<LibraryGameItem>().Single(game => game.DataKey == "b"));
        Assert.Equal("keep my draft", otherDraft.Name); Assert.True(otherDraft.IsDirty);
        model.LaunchCommand.Execute(null); Assert.Equal(1, launches);
    });

    [Fact]
    public Task DelayedSaveErrorNeverOffersToDiscardAnotherGamesDraft() => OnDispatcher(async () =>
    {
        var failedSave = new TaskCompletionSource<GameLibraryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new[] { "a", "b" }.Select(key => new LibraryGame { Config = new GameConfig { DataKey = key,
            Executable = @"C:\" + key + ".exe", DisplayName = key },
            Installation = new(GameInstallationState.Installed, "test") }).ToArray();
        var backend = new Moq.Mock<IGameLibrary>();
        backend.Setup(value => value.ExecuteAsync(Moq.It.IsAny<GameLibraryRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns<GameLibraryRequest, CancellationToken>((request, _) => request.Operation == GameLibraryOperation.Save
                ? failedSave.Task : Task.FromResult(new GameLibraryResponse { Success = true, Games = rows }));
        using var model = new LibraryViewModel(backend.Object);
        await model.RefreshAsync();
        model.Selected = model.Games.Cast<LibraryGameItem>().Single(game => game.DataKey == "a");
        model.Selected.Name = "A draft"; model.LaunchCommand.Execute(null);
        Assert.True(model.IsBusy);
        var b = model.Games.Cast<LibraryGameItem>().Single(game => game.DataKey == "b");
        model.Selected = b; b.Name = "B draft";
        failedSave.SetResult(new GameLibraryResponse { Error = "conflict" });
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        Assert.False(model.IsBusy); Assert.Same(b, model.Selected);
        Assert.False(model.HasActionError); Assert.Equal("B draft", b.Name); Assert.True(b.IsDirty);
    });

    [Fact]
    public Task SaveThenLaunch_UsesConfirmedSettingsAndClearsDirtyState() => OnSta(() =>
    {
        var backend = new Backend();
        using var model = new LibraryViewModel(backend);
        model.RefreshAsync().GetAwaiter().GetResult();
        model.Selected!.Hdr = true;
        model.LaunchCommand.Execute(null);
        Assert.Equal(1, backend.Saves);
        Assert.Equal(1, backend.Launches);
        Assert.True(backend.Game.HdrEnabled);
        Assert.False(model.Selected!.IsDirty);
    });

    [Fact]
    public Task UnsavedInvalidText_SurvivesUiRestart_WithoutChangingConfiguration() => OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHelper.Desktop.Tests." + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "config.yml");
        var backend = new Backend();
        try
        {
            using (var first = new LibraryViewModel(backend, path))
            {
                first.RefreshAsync().GetAwaiter().GetResult();
                first.Selected!.UseSpeed = true;
                first.Selected.Speed = "unfinished input";
            }
            using var second = new LibraryViewModel(backend, path);
            second.RefreshAsync().GetAwaiter().GetResult();
            Assert.Equal("unfinished input", second.Selected!.Speed);
            Assert.True(second.Selected.IsDirty);
            Assert.Null(backend.Game.SpeedMultiplier);
            Assert.Equal(0, backend.Saves);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public Task ControllerCompanion_DraftSurvivesRestartAndCanBeDisabledWithoutAffectingGame() => OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-controller-draft", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "config.yml"); var backend = new Backend();
        try
        {
            using (var model = new LibraryViewModel(backend, path))
            {
                model.RefreshAsync().GetAwaiter().GetResult(); Assert.False(model.Selected!.UseDs4Windows);
                model.Selected.UseDs4Windows = true; model.Selected.Ds4WindowsPath = @"C:\Controller\DS4Windows.exe";
            }
            using var reopened = new LibraryViewModel(backend, path); reopened.RefreshAsync().GetAwaiter().GetResult();
            Assert.True(reopened.Selected!.UseDs4Windows); Assert.True(reopened.Selected.IsDirty);
            Assert.Equal(@"C:\Controller\DS4Windows.exe", reopened.Selected.ToConfig().Launch!.Ds4WindowsPath);
            reopened.SaveCommand.Execute(null); Assert.Equal(@"C:\Controller\DS4Windows.exe", backend.Game.Launch!.Ds4WindowsPath);
            reopened.Selected!.UseDs4Windows = false; reopened.SaveCommand.Execute(null);
            Assert.Null(backend.Game.Launch!.Ds4WindowsPath); Assert.Equal(@"C:\a.exe", backend.Game.Launch.Target);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public Task PassivePolling_DoesNotDisableMenusOrResetUnchangedGameView() => OnSta(() =>
    {
        using var model = new LibraryViewModel(new Backend()); model.RefreshAsync().GetAwaiter().GetResult();
        var selected = model.Selected;
        var ready = new List<bool>(); var resets = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Ready)) ready.Add(model.Ready); };
        model.Games.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        model.RefreshAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(false, ready);
        Assert.Equal(0, resets); Assert.Same(selected, model.Selected);
    });

    [Fact]
    public Task FirstConnection_NotifiesBoundLaunchButtonWhenItBecomesAvailable() => OnSta(() =>
    {
        using var model = new LibraryViewModel(new Backend());
        var buttonEnabled = model.LaunchCommand.CanExecute(null);
        model.LaunchCommand.CanExecuteChanged += (_, _) => buttonEnabled = model.LaunchCommand.CanExecute(null);
        Assert.False(buttonEnabled); model.RefreshAsync().GetAwaiter().GetResult(); Assert.True(buttonEnabled);
    });

    [Theory]
    [InlineData("运行中")]
    [InlineData("运行中·外部")]
    public Task RecentActivity_OrdersRunningThenNewestPlayedOrAddedAndRetainsDraftOnMove(string state) => OnSta(() =>
    {
        var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        LibraryGame Row(string key, DateTimeOffset? added = null, DateTimeOffset? played = null, string state = "未运行") => new()
        { Config = new() { DataKey = key, Executable = key + ".exe", DisplayName = key, AddedAt = added }, LastPlayedAt = played, State = state, Installation = new(GameInstallationState.Installed, "test") };
        var rows = new List<LibraryGame> { Row("played", played: now.AddHours(-1)), Row("added", added: now), Row("running", state: state), Row("legacy1"), Row("legacy2") };
        var backend = new Moq.Mock<IGameLibrary>();
        backend.Setup(item => item.ExecuteAsync(Moq.It.IsAny<GameLibraryRequest>(), Moq.It.IsAny<CancellationToken>())).ReturnsAsync(() => new GameLibraryResponse { Success = true, Games = rows });
        using var model = new LibraryViewModel(backend.Object); model.RefreshAsync().GetAwaiter().GetResult();
        Assert.Equal(new[] { "running", "added", "played", "legacy2", "legacy1" }, model.Games.Cast<LibraryGameItem>().Select(item => item.DataKey));
        var selected = model.Games.Cast<LibraryGameItem>().Single(item => item.DataKey == "played"); model.Selected = selected; selected.Name = "Draft";
        rows[0] = Row("played", played: now.AddMinutes(1)); model.RefreshAsync().GetAwaiter().GetResult();
        Assert.Equal("played", model.Games.Cast<LibraryGameItem>().ElementAt(1).DataKey);
        Assert.Same(selected, model.Selected); Assert.Equal("Draft", selected.Name); Assert.True(selected.IsDirty);
    });

    [Theory]
    [InlineData("运行中")]
    [InlineData("运行中·外部")]
    public Task RunningGameState_ShowsLiveSessionTimerAndClearsAfterStop(string state) => OnSta(() =>
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero));
        var backend = new Backend();
        using var model = new LibraryViewModel(backend, null, null, clock);
        model.RefreshAsync().GetAwaiter().GetResult();
        var item = model.Selected!;
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        backend.State = state;
        backend.StartedAt = clock.LocalStamp;
        model.RefreshAsync().GetAwaiter().GetResult();
        Assert.Same(item, model.Selected);
        Assert.True(item.IsRunning);
        Assert.False(model.LaunchCommand.CanExecute(null));
        Assert.Contains(nameof(LibraryGameItem.IsRunning), changed);
        Assert.Equal(state, item.StateDisplay);
        Assert.Null(item.RunningSessionLine);

        clock.Advance(TimeSpan.FromSeconds(12));
        model.OnClockTick();
        Assert.Equal($"{state} · 00:00:12", item.StateDisplay);
        Assert.Equal("本次已玩 00:00:12", item.RunningSessionLine);

        clock.Advance(TimeSpan.FromSeconds(48));
        model.OnClockTick();
        Assert.Equal($"{state} · 00:01:00", item.StateDisplay);

        // 停止后清空计时，徽章和详情回到普通状态文本。
        changed.Clear();
        backend.State = "未运行";
        model.RefreshAsync().GetAwaiter().GetResult();
        Assert.False(item.IsRunning);
        Assert.Contains(nameof(LibraryGameItem.IsRunning), changed);
        Assert.Equal("未运行", item.StateDisplay);
        Assert.Null(item.RunningSessionLine);
    });

    private static Task OnDispatcher(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            }));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class Backend : IGameLibrary
    {
        public GameConfig Game { get; private set; } = new() { DataKey = "a", DisplayName = "A", Executable = @"C:\a.exe" };
        public string State { get; set; } = "未运行";
        public DateTime? StartedAt { get; set; }
        public GameInstallationState Installation { get; set; } = GameInstallationState.Installed;
        public bool RejectSave { get; init; }
        public int Saves { get; private set; }
        public int Launches { get; private set; }
        public Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation == GameLibraryOperation.Save)
            {
                Saves++;
                if (RejectSave) return Task.FromResult(new GameLibraryResponse { Error = "conflict" });
                Assert.Equal(GameConfigRevision.For(Game), request.ExpectedRevision);
                Game = request.Game!;
            }
            if (request.Operation == GameLibraryOperation.Launch) { Launches++; Assert.True(Game.HdrEnabled); }
            if (request.Operation == GameLibraryOperation.Statistics)
                return Task.FromResult(new GameLibraryResponse
                {
                    Success = true,
                    Statistics = new PlaytimeDashboard
                    {
                        Today = new(2026, 4, 21), Start = new(2026, 4, 1), Days = 30, Minutes = 600, Sessions = 1, ActiveDays = 1, GamesPlayed = 1,
                        Games = [new("a", "A", 600, 1, DateTime.UnixEpoch)]
                    }
                });
            return Task.FromResult(new GameLibraryResponse
            {
                Success = true, MonitorRunning = true,
                Games = [new() { Config = Game, Revision = GameConfigRevision.For(Game), State = State, RunningStartedAt = StartedAt, Installation = new(Installation, "test") }]
            });
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public DateTimeOffset LocalDateTime => _now.DateTime;
        public DateTime LocalStamp => _now.DateTime;
        public void Advance(TimeSpan duration) => _now += duration;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
