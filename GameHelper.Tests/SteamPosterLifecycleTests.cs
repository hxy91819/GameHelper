using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Desktop;
using GameHelper.Infrastructure.Artwork;
using Moq;

namespace GameHelper.Tests;

public sealed class SteamPosterLifecycleTests
{
    [Fact]
    public Task PassivePolling_StaleResponseCannotOverwriteACompletedSave() => OnSta(async () =>
    {
        var stale = new TaskCompletionSource<GameHelper.Core.Models.GameLibraryResponse>();
        var original = new GameHelper.Core.Models.GameConfig { DataKey = "a", Executable = "a.exe", DisplayName = "Original" };
        GameHelper.Core.Models.GameLibraryResponse Reply(GameHelper.Core.Models.GameConfig game) => new()
        { Success = true, Games = [new() { Config = game, Revision = GameHelper.Core.Utilities.GameConfigRevision.For(game), Installation = new(GameHelper.Core.Models.GameInstallationState.Installed, "test") }] };
        var calls = 0; var saved = new TaskCompletionSource();
        var backend = new Mock<IGameLibrary>();
        backend.Setup(item => item.ExecuteAsync(It.IsAny<GameHelper.Core.Models.GameLibraryRequest>(), It.IsAny<CancellationToken>()))
            .Returns((GameHelper.Core.Models.GameLibraryRequest request, CancellationToken _) =>
            {
                if (request.Operation == GameHelper.Core.Models.GameLibraryOperation.Save) { saved.SetResult(); return Task.FromResult(Reply(request.Game!)); }
                return ++calls == 1 ? Task.FromResult(Reply(original)) : stale.Task;
            });
        using var model = new LibraryViewModel(backend.Object); await model.RefreshAsync();
        var polling = model.RefreshAsync(); model.Selected!.Name = "Saved"; model.SaveCommand.Execute(null); await saved.Task;
        stale.SetResult(Reply(original)); await polling;
        Assert.Equal("Saved", model.Selected!.Name); Assert.False(model.Selected.IsDirty); Assert.True(model.Ready);
    });

    [Fact]
    public Task PosterSearch_DisabledGameListRetainsDarkBackground() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-poster-theme", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var handler = new PendingSearchHandler(); using var http = new HttpClient(handler);
            var posters = new SteamPosterLibrary(Path.Combine(root, "config.yml"), true, new SteamArtworkClient(http, root, _ => false), _ => null);
            var backend = new Mock<IGameLibrary>();
            backend.Setup(item => item.ExecuteAsync(It.IsAny<GameHelper.Core.Models.GameLibraryRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GameHelper.Core.Models.GameLibraryResponse
            { Success = true, Games = [new() { Config = new() { DataKey = "a", Executable = "unlocated.exe", DisplayName = "Astral Ascent" }, Installation = new(GameHelper.Core.Models.GameInstallationState.Installed, "test") }] });
            using var model = new LibraryViewModel(backend.Object, null, posters); await model.RefreshAsync();
            var window = new MissingPostersWindow(model) { ShowActivated = false, ShowInTaskbar = false };
            WindowSnapshot.PrepareOffscreen(window);
            try
            {
                window.Show(); await handler.Started.Task;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                var list = Descendants<System.Windows.Controls.ListBox>(window).First(); Assert.False(list.IsEnabled);
                var point = list.TranslatePoint(new System.Windows.Point(120, 160), window);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var pixel = new byte[4]; bitmap.CopyPixels(new System.Windows.Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                Assert.True(pixel[0] < 90 && pixel[1] < 90 && pixel[2] < 90, $"Disabled poster list became light: BGR={pixel[0]},{pixel[1]},{pixel[2]}");
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public Task PosterSearch_AutomaticallyAssociatesLocalizedBaseGameAndLoadsCover() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-poster-auto", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var bitmap = BitmapSource.Create(200, 300, 96, 96, PixelFormats.Bgra32, null, new byte[200 * 300 * 4], 800);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var bytes = new MemoryStream(); encoder.Save(bytes);
            using var http = new HttpClient(new AutomaticSearchHandler(bytes.ToArray()));
            var path = Path.Combine(root, "config.yml");
            var posters = new SteamPosterLibrary(path, true, new SteamArtworkClient(http, Path.Combine(root, "cache"), LocalGameArtwork.IsValidPoster), _ => null);
            var backend = new Mock<IGameLibrary>();
            backend.Setup(item => item.ExecuteAsync(It.IsAny<GameHelper.Core.Models.GameLibraryRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GameHelper.Core.Models.GameLibraryResponse
            { Success = true, Games = [new() { Config = new() { DataKey = "astral", Executable = "unlocated.exe", DisplayName = "Astral Ascent" }, Installation = new(GameHelper.Core.Models.GameInstallationState.Installed, "test") }] });
            using var model = new LibraryViewModel(backend.Object, null, posters); await model.RefreshAsync();
            var game = model.Selected!; var loaded = new TaskCompletionSource();
            game.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(game.ArtworkIsCover) && game.ArtworkIsCover) loaded.TrySetResult(); };
            var window = new MissingPostersWindow(model) { ShowActivated = false, ShowInTaskbar = false };
            WindowSnapshot.PrepareOffscreen(window);
            try
            {
                window.Show(); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(posters.HasAssociation("astral"));
                Assert.Contains("1280930", File.ReadAllText(path + ".desktop-artwork.json"));
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    private sealed class AutomaticSearchHandler(byte[] image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "shared.steamstatic.com")
            {
                var content = new ByteArrayContent(image); content.Headers.ContentType = new("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            var json = request.RequestUri.Host == "store.steampowered.com"
                ? """{"items":[{"id":1280930,"name":"星界战士","type":"app"},{"id":2633220,"name":"Astral Ascent Artbook","type":"app"}]}"""
                : """{"response":{"store_items":[{"appid":1280930,"name":"Astral Ascent","type":0},{"appid":2633220,"name":"Astral Ascent Artbook","type":4}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T found) yield return found; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private sealed class PendingSearchHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return new(HttpStatusCode.OK); }
    }

    [Fact]
    public Task ClosingPreviewCancelsOnlyItsWaitAndReopeningReusesSuccessfulSharedDownload() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-preview-lifetime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = BitmapSource.Create(200, 300, 96, 96, PixelFormats.Bgra32, null, new byte[200 * 300 * 4], 800);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var bytes = new MemoryStream(); encoder.Save(bytes);
            using var handler = new DelayedHandler(bytes.ToArray());
            using var http = new HttpClient(handler);
            var client = new SteamArtworkClient(http, root, LocalGameArtwork.IsValidPoster);
            var posters = new SteamPosterLibrary(null, true, client, _ => null);
            using var model = new LibraryViewModel(Mock.Of<IGameLibrary>(), null, posters);
            using var dialog = new CancellationTokenSource();
            var first = model.PreviewPosterAsync(620, dialog.Token);
            await handler.Started.Task;
            dialog.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(handler.RequestToken.IsCancellationRequested);
            var reopened = model.PreviewPosterAsync(620, CancellationToken.None);
            handler.Release.SetResult();
            Assert.NotNull(await reopened);
            Assert.Equal(1, handler.MetadataRequests);
            Assert.NotNull(await model.PreviewPosterAsync(620, CancellationToken.None));
            Assert.Equal(1, handler.MetadataRequests);
        }
        finally { Directory.Delete(root, true); }
    });

    private sealed class DelayedHandler(byte[] image) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }
        public int MetadataRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.steampowered.com")
            {
                MetadataRequests++; RequestToken = cancellationToken; Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.NotFound);
            }
            var content = new ByteArrayContent(image); content.Headers.ContentType = new("image/png");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private static Task OnSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
