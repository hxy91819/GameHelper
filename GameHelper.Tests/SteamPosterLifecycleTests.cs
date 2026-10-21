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
