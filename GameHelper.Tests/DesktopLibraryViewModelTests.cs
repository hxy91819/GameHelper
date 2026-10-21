using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class DesktopLibraryViewModelTests
{
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
            return Task.FromResult(new GameLibraryResponse
            {
                Success = true, MonitorRunning = true,
                Games = [new() { Config = Game, Revision = GameConfigRevision.For(Game), State = State }]
            });
        }
    }
}
