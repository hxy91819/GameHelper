using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.Desktop;
using Moq;

namespace GameHelper.Tests;

public sealed class DesktopLibraryViewModelTests
{
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

    [Fact]
    public Task RecentActivity_OrdersRunningThenNewestPlayedOrAddedAndRetainsDraftOnMove() => OnSta(() =>
    {
        var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        LibraryGame Row(string key, DateTimeOffset? added = null, DateTimeOffset? played = null, string state = "未运行") => new()
        { Config = new() { DataKey = key, Executable = key + ".exe", DisplayName = key, AddedAt = added }, LastPlayedAt = played, State = state, Installation = new(GameInstallationState.Installed, "test") };
        var rows = new List<LibraryGame> { Row("played", played: now.AddHours(-1)), Row("added", added: now), Row("running", state: "运行中"), Row("legacy1"), Row("legacy2") };
        var backend = new Moq.Mock<IGameLibrary>();
        backend.Setup(item => item.ExecuteAsync(Moq.It.IsAny<GameLibraryRequest>(), Moq.It.IsAny<CancellationToken>())).ReturnsAsync(() => new GameLibraryResponse { Success = true, Games = rows });
        using var model = new LibraryViewModel(backend.Object); model.RefreshAsync().GetAwaiter().GetResult();
        Assert.Equal(new[] { "running", "added", "played", "legacy2", "legacy1" }, model.Games.Cast<LibraryGameItem>().Select(item => item.DataKey));
        var selected = model.Games.Cast<LibraryGameItem>().Single(item => item.DataKey == "played"); model.Selected = selected; selected.Name = "Draft";
        rows[0] = Row("played", played: now.AddMinutes(1)); model.RefreshAsync().GetAwaiter().GetResult();
        Assert.Equal("played", model.Games.Cast<LibraryGameItem>().ElementAt(1).DataKey);
        Assert.Same(selected, model.Selected); Assert.Equal("Draft", selected.Name); Assert.True(selected.IsDirty);
    });

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
                Games = [new() { Config = Game, Revision = GameConfigRevision.For(Game), State = State, Installation = new(Installation, "test") }]
            });
        }
    }
}
