using System.Windows.Media;
using System.Windows.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class StatisticsViewModelTests
{
    [Fact]
    public Task JournalFilterAndEditor_UpdateBothSearchAndEmptyStateWithoutIpc() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-journal-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryJournalStore(Path.Combine(root, "config.yml"));
            var backend = new Backend((query, _) => Task.FromResult(Response(query.Days, 600)));
            using var model = new StatisticsViewModel(backend, null, store, TimeProvider.System);
            await model.RefreshAsync();
            var row = model.Games.Cast<StatisticsGameRow>().Single();
            model.SelectedJournalStatus = "已通关";
            Assert.True(model.Games.IsEmpty);
            Assert.False(model.HasMatchingGames);
            var editor = model.EditJournal(row);
            editor.Status = "已通关";
            editor.CompletedOn = new DateTime(2032, 2, 29);
            editor.Note = "完成旅程";
            Assert.True(editor.Save());
            Assert.True(model.HasMatchingGames);
            Assert.Equal("完成旅程", model.Games.Cast<StatisticsGameRow>().Single().Note);
            Assert.Contains("2032/02/29", row.JournalSummary);
            model.Search = "missing";
            Assert.False(model.HasMatchingGames);
            model.Search = "A";
            Assert.True(model.HasMatchingGames);
            model.SelectedJournalStatus = "所有状态";
            Assert.True(model.HasMatchingGames);
            Assert.Single(backend.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public Task LatestRangeWinsWhenServerIgnoresCancellation() => OnSta(async () =>
    {
        var first = new TaskCompletionSource<GameLibraryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<GameLibraryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend((query, index) => index == 0 ? first.Task : second.Task);
        using var model = new StatisticsViewModel(backend);
        var initial = model.RefreshAsync();
        var latest = model.SetRangeAsync(7);
        second.SetResult(Response(7, 120)); await latest;
        first.SetResult(Response(30, 600)); await initial;
        Assert.Equal(7, model.Days);
        Assert.Equal("2.0", model.TotalHours);
        Assert.False(model.IsBusy);
    });

    [Fact]
    public Task SummaryDrilldownAndDateSelection_AreReadOnlyAndKeepOverview() => OnSta(async () =>
    {
        var backend = new Backend((query, _) => Task.FromResult(Response(query.Days, 600) with
        { Statistics = Data(query.Days, 600) with { Offset = query.Offset, RecentTotal = 65, Recent = [new("a", "A", DateTime.UnixEpoch, DateTime.UnixEpoch, 20)] } }));
        using var model = new StatisticsViewModel(backend);
        await model.EnsureLoadedAsync();
        model.Search = "not found"; Assert.True(model.Games.IsEmpty);
        model.Search = "A"; Assert.False(model.Games.IsEmpty);
        var game = Assert.IsType<StatisticsGameRow>(model.Games.Cast<object>().Single());
        await model.SelectGameAsync(game);
        Assert.Equal("a", backend.Requests[^1].SessionGameKey);
        Assert.Equal("10.0", model.TotalHours);
        Assert.False(backend.Requests[^1].IncludeRecent);
        await model.SelectDateAsync(new DateOnly(2026, 4, 20));
        Assert.Equal(new DateOnly(2026, 4, 20), backend.Requests[^1].FocusDate);
        Assert.Equal("a", backend.Requests[^1].SessionGameKey);
        Assert.Equal("10.0", model.TotalHours);
        await model.SelectGameAsync(null);
        Assert.Null(backend.Requests[^1].SessionGameKey);
        Assert.Equal(0, backend.Requests[^1].Offset);
        Assert.Equal("游玩节奏", model.RecentTitle);
        Assert.Null(model.FocusDate);
    });

    [Fact]
    public Task FailedRangeChangeRetainsLastSuccessfulRangeAndCanRetry() => OnSta(async () =>
    {
        var backend = new Backend((query, index) => Task.FromResult(index == 1 ? new GameLibraryResponse { Error = "read failed" } : Response(query.Days, 180)));
        using var model = new StatisticsViewModel(backend);
        await model.RefreshAsync();
        await model.SetRangeAsync(7);
        Assert.Equal(30, model.Days);
        Assert.Equal("3.0", model.TotalHours);
        // 失败提示只给动作指引，不透出底层错误细节（spec-leak review 口径）。
        Assert.Equal("统计暂不可用，请刷新重试。", model.Status);
        await model.SetRangeAsync(7);
        Assert.Equal(7, model.Days);
        Assert.DoesNotContain("read failed", model.Status);
    });

    [Fact]
    public Task ClosingPageOwnerCancelsRequestAndRejectsLateResponse() => OnSta(async () =>
    {
        var pending = new TaskCompletionSource<GameLibraryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend((_, _) => pending.Task);
        var model = new StatisticsViewModel(backend);
        var request = model.RefreshAsync(); model.Dispose();
        pending.SetResult(Response(30, 999)); await request;
        Assert.False(model.HasHistory);
        Assert.False(model.RefreshCommand.CanExecute(null));
    });

    [Fact]
    public Task PosterLookupFillsRowsFavouriteAndRefreshesInPlace() => OnSta(async () =>
    {
        var backend = new Backend((query, _) => Task.FromResult(Response(query.Days, 600) with
        {
            Statistics = Data(query.Days, 600) with { Recent = [new("a", "A", DateTime.UnixEpoch, DateTime.UnixEpoch, 20)] }
        }));
        ImageSource? artwork = new DrawingImage();
        using var model = new StatisticsViewModel(backend, _ => artwork);
        await model.EnsureLoadedAsync();
        var row = Assert.IsType<StatisticsGameRow>(model.Games.Cast<object>().Single());
        Assert.Same(artwork, row.Artwork);
        Assert.Same(artwork, model.Recent.Single().Artwork);
        Assert.Same(artwork, model.FavouriteArtwork);
        Assert.True(model.HasFavourite);
        artwork = null;
        model.RefreshArtwork();
        Assert.Null(row.Artwork);
        Assert.Null(model.FavouriteArtwork);
    });

    [Fact]
    public Task CalendarKeepsWholeYearAcrossRangeChanges() => OnSta(async () =>
    {
        var today = new DateOnly(2026, 4, 21);
        var days = Enumerable.Range(0, 365).Select(index => new DashboardDay(today.AddDays(index - 364), index, 1)).ToArray();
        var backend = new Backend((query, _) => Task.FromResult(Response(query.Days, 60) with
        { Statistics = Data(query.Days, 60) with { Calendar = days, Start = today.AddDays(1 - Math.Max(1, query.Days)) } }));
        using var model = new StatisticsViewModel(backend);
        await model.RefreshAsync();
        Assert.Equal(days, model.Calendar);
        var width = model.CalendarWidth;
        await model.SetRangeAsync(7);
        Assert.Equal(days, model.Calendar);
        Assert.Equal(width, model.CalendarWidth);
        await model.SetRangeAsync(0);
        Assert.Equal(days, model.Calendar);
    });

    private static GameLibraryResponse Response(int days, long minutes) => new() { Success = true, Statistics = Data(days, minutes) };
    private static PlaytimeDashboard Data(int days, long minutes) => new()
    {
        Days = days, Minutes = minutes, Sessions = 1, GamesPlayed = 1, ActiveDays = 1,
        Today = new(2026, 4, 21), Start = new(2026, 4, 1), Games = [new("a", "A", minutes, 1, DateTime.UnixEpoch)]
    };
    private sealed class Backend(Func<PlaytimeDashboardQuery, int, Task<GameLibraryResponse>> respond) : IGameLibrary
    {
        public List<PlaytimeDashboardQuery> Requests { get; } = [];
        public Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(GameLibraryOperation.Statistics, request.Operation);
            var query = Assert.IsType<PlaytimeDashboardQuery>(request.Statistics);
            Requests.Add(query);
            return respond(query, Requests.Count - 1);
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
