using System.Windows.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Desktop;
using GameHelper.Infrastructure.Providers;
using Moq;

namespace GameHelper.Tests;

public sealed class AdultCollectionTests
{
    private static LibraryGame Row(string key, bool adult, bool running = false) => new()
    {
        Config = new() { DataKey = key, DisplayName = key, Executable = key + ".exe", Adult = adult },
        Revision = key, State = running ? "运行中" : "未运行", LastPlayedAt = DateTimeOffset.UnixEpoch,
        Installation = new(GameInstallationState.Installed, "fixture")
    };

    [Fact]
    public Task DefaultSearchGenresContinueAndConsoleNeverIncludeAdultEvenWhileRunning() => OnDispatcher(async () =>
    {
        var rows = new[] { Row("ordinary", false), Row("private-title", true, true) };
        var backend = new Mock<IGameLibrary>();
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GameLibraryResponse { Success = true, Games = rows });
        using var model = new LibraryViewModel(backend.Object); await model.RefreshAsync();
        foreach (var game in model.ConsoleGames) game.SetGenres(["动作"]);
        Assert.Equal("ordinary", Assert.Single(model.Games.Cast<LibraryGameItem>()).DataKey);
        Assert.Equal("ordinary", model.ContinueGame!.DataKey);
        Assert.Equal("ordinary", Assert.Single(model.ConsoleShelfGames).DataKey);
        Assert.Equal("private-title", Assert.Single(model.ConsoleCollectionGames(true)).DataKey);
        Assert.DoesNotContain(model.MissingPosters(true), game => game.SavedAdult);
        Assert.DoesNotContain("private-title", model.ConsoleRunningSummary(false));
        Assert.Contains("1 款隐藏游戏", model.ConsoleRunningSummary(false));
        Assert.Contains("private-title", model.ConsoleRunningSummary(true));
        Assert.Equal(1, model.GenreGroups.Single(g => g.Name == "动作").Count);
        Assert.Equal(1, model.GenreGroups.Single(g => g.Name == LibraryViewModel.AdultCollection).Count);
        model.Search = "private-title"; Assert.True(model.Games.IsEmpty); Assert.Null(model.Selected); Assert.Null(model.ContinueGame);
        model.Search = ""; model.SetGenre("动作"); Assert.Equal("ordinary", Assert.Single(model.Games.Cast<LibraryGameItem>()).DataKey);
        model.SetGenre(LibraryViewModel.AdultCollection); Assert.Equal("private-title", Assert.Single(model.Games.Cast<LibraryGameItem>()).DataKey);
        Assert.All(model.MissingPosters(true), game => Assert.True(game.SavedAdult));
        model.SetGenre(null); Assert.Equal("ordinary", model.Selected!.DataKey); Assert.Equal("01", model.CatalogCount);
    });

    [Fact]
    public Task AuthoritativeClassificationOverridesRetainedDirtyDraftAndResetsOnNewLibrary() => OnDispatcher(async () =>
    {
        var rows = new[] { Row("ordinary", false), Row("target", false) };
        var backend = new Mock<IGameLibrary>();
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GameLibraryResponse { Success = true, Games = rows });
        using var model = new LibraryViewModel(backend.Object); await model.RefreshAsync();
        model.SetInstallationGroup("all");
        var target = model.ConsoleGames.Single(g => g.DataKey == "target"); target.Name = "unsaved draft";
        model.Selected = model.ConsoleGames.Single(g => g.DataKey == "ordinary");
        rows = [rows[0], Row("target", true) with { Revision = "new" }];
        await model.RefreshAsync();
        Assert.Same(target, model.ConsoleGames.Single(g => g.DataKey == "target"));
        Assert.True(target.IsDirty); Assert.True(target.SavedAdult); Assert.DoesNotContain(target, model.Games.Cast<LibraryGameItem>());
        model.SetGenre(LibraryViewModel.AdultCollection); Assert.Same(target, Assert.Single(model.Games.Cast<LibraryGameItem>()));
        using var reopened = new LibraryViewModel(backend.Object); await reopened.RefreshAsync();
        Assert.False(reopened.InAdultCollection); Assert.DoesNotContain(reopened.Games.Cast<LibraryGameItem>(), game => game.SavedAdult);
    });

    [Fact]
    public void UnsavedClassificationAndDraftRecoveryDoNotChangeSavedVisibility()
    {
        var original = new LibraryGameItem(Row("ordinary", false)) { Adult = true };
        Assert.False(original.SavedAdult); Assert.True(original.ToConfig().Adult);
        var restored = LibraryGameItem.RestoreDraft(original.ExportDraft());
        Assert.True(restored.Adult); Assert.False(restored.SavedAdult);
        var legacy = original.ExportDraft() with { Baseline = Row("adult", true), Adult = null };
        Assert.True(LibraryGameItem.RestoreDraft(legacy).Adult);
    }

    [Fact]
    public Task AdultChangesPersistEvenAfterAnotherFieldAlreadyMadeTheDraftDirty() => OnDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-adult-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "config.yml"); File.WriteAllText(path, "games: []");
        var backend = new Mock<IGameLibrary>();
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLibraryResponse { Success = true, ConfigPath = path, Games = [Row("game", false)] });
        try
        {
            using var model = new LibraryViewModel(backend.Object, path, new SteamPosterLibrary(path, allowNetwork: false));
            await model.RefreshAsync(); var item = model.Selected!;
            item.Name = "dirty before classification"; await model.FlushDraftsAsync();
            item.Adult = true; await model.FlushDraftsAsync();
            Assert.True(new LibraryDraftStore(path).Read().Single().Adult);
            item.Adult = false; await model.FlushDraftsAsync();
            Assert.False(new LibraryDraftStore(path).Read().Single().Adult);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ConsoleSettingsSavesAndClearsClassificationWithExistingRevision()
    {
        var requests = new List<GameLibraryRequest>();
        var backend = new Mock<IGameLibrary>();
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GameLibraryRequest, CancellationToken>((r, _) => { requests.Add(r); return Task.FromResult(new GameLibraryResponse { Success = true }); });
        var draft = new ConsoleGameSettings(backend.Object, Row("game", false)) { Adult = true };
        await draft.SaveAsync(); Assert.True(requests[^1].Game!.Adult);
        draft.Adult = false; await draft.SaveAsync(); Assert.False(requests[^1].Game!.Adult);
        Assert.All(requests, r => { Assert.Equal("game", r.ExpectedRevision); Assert.Equal(GameLibraryOperation.Save, r.Operation); });
    }

    [Fact]
    public void CatalogAndYamlRoundTripClassificationAndPreserveItOnUnrelatedEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-adult-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); var path = Path.Combine(root, "config.yml");
            var config = new YamlConfigProvider(path);
            config.Change(c => c.Games = [Row("game", true).Config]);
            Assert.Contains("adult: true", File.ReadAllText(path));
            var catalog = new GameCatalogService(config);
            catalog.Update("game", new() { DisplayName = "Changed" });
            Assert.True(config.Read().Games.Single().Adult); Assert.True(catalog.List().Single().Adult);
            catalog.Update("game", new() { Adult = false });
            Assert.False(config.Read().Games.Single().Adult); Assert.DoesNotContain("adult:", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Task OnDispatcher(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
