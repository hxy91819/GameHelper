using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Desktop;
using Moq;

namespace GameHelper.Tests;

public sealed class SessionRatingWindowTests
{
    [Fact]
    public Task ThoughtCounterAndClearActionReflectTheLoadedRating() => OnDispatcher(async () =>
    {
        var window = new SessionRatingWindow(Mock.Of<IGameLibrary>(), Row() with { Rating = null });
        try
        {
            await Layout(window);
            var model = Assert.IsType<SessionRatingEditorViewModel>(window.DataContext);
            Assert.False(model.HasRating);
            var clear = Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "清除评分"));
            Assert.Equal(Visibility.Collapsed, clear.Visibility);
            Descendants(window).OfType<TextBox>().Single().Text = "新的想法🎮";
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal("6 / 2000", model.ThoughtCount);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text == model.ThoughtCount);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public Task ClickingStar_SavesThatScoreAndThoughtWithRevision_AndCloses(int score) => OnDispatcher(async () =>
    {
        var row = Row();
        var backend = new Mock<IGameLibrary>(MockBehavior.Strict);
        backend.Setup(b => b.ExecuteAsync(It.Is<GameLibraryRequest>(r =>
            r.Operation == GameLibraryOperation.SaveSessionRating && r.SessionRating == new SaveSessionRating(row.SessionId, score, "本局想法", row.Revision)), default))
            .ReturnsAsync(new GameLibraryResponse { Success = true, RatedSession = row with { Revision = row.Revision + 1 } });
        var window = new SessionRatingWindow(backend.Object, row);
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            await Layout(window);
            var buttons = Stars(window);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, buttons.Select(b => (int)b.Tag));
            Descendants(window).OfType<TextBox>().Single().Text = "本局想法";
            Click(buttons[score - 1]);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.True(closed);
            backend.VerifyAll(); backend.VerifyNoOtherCalls();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task PendingClick_RejectsAnotherStar_AndFailureKeepsFieldsAndVisibleError() => OnDispatcher(async () =>
    {
        var pending = new TaskCompletionSource<GameLibraryResponse>();
        var backend = new Mock<IGameLibrary>(MockBehavior.Strict);
        var requests = new List<GameLibraryRequest>();
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), default))
            .Callback<GameLibraryRequest, CancellationToken>((r, _) => requests.Add(r)).Returns(pending.Task);
        var window = new SessionRatingWindow(backend.Object, Row());
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            await Layout(window);
            var model = Assert.IsType<SessionRatingEditorViewModel>(window.DataContext);
            var thought = Descendants(window).OfType<TextBox>().Single(); thought.Text = "失败后保留";
            var stars = Stars(window); Click(stars[4]);
            Assert.True(model.Busy);
            // Raise a second event even before WPF applies the disabled binding: it must not change the draft.
            Click(stars[0]);
            Assert.Equal(5, model.Stars); Assert.Single(requests);
            pending.SetResult(new GameLibraryResponse { Error = "评分版本冲突，请重载" });
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.False(closed); Assert.False(model.Busy);
            Assert.Equal(5, model.Stars); Assert.Equal("失败后保留", thought.Text);
            Assert.Equal(5, Stars(window).Count(b => Equals(b.Content, "★")));
            Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text == "评分版本冲突，请重载");
            Assert.Equal("失败后保留", requests.Single().SessionRating!.Thought);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task InitialLoad_Preview_Focus_Reload_AndCancelNeverSave() => OnDispatcher(async () =>
    {
        var row = Row();
        var backend = new Mock<IGameLibrary>(MockBehavior.Strict);
        var updated = row with { Rating = new SessionRating(2, "最新想法", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 9), Revision = 9 };
        backend.Setup(b => b.ExecuteAsync(It.Is<GameLibraryRequest>(r => r.Operation == GameLibraryOperation.GetSessionRating && r.SessionId == row.SessionId), default))
            .ReturnsAsync(new GameLibraryResponse { Success = true, RatedSession = updated });
        var window = new SessionRatingWindow(backend.Object, row);
        try
        {
            await Layout(window);
            var stars = Stars(window);
            Assert.Equal(4, stars.Count(b => Equals(b.Content, "★")));
            stars[4].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.Equal(5, stars.Count(b => Equals(b.Content, "★")));
            stars[4].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            stars[1].Focus();
            Assert.Equal(4, Assert.IsType<SessionRatingEditorViewModel>(window.DataContext).Stars);
            backend.VerifyNoOtherCalls();
            Click(Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "重载")));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal(2, stars.Count(b => Equals(b.Content, "★")));
            Assert.Equal("最新想法", Descendants(window).OfType<TextBox>().Single().Text);
            window.Close();
            backend.Verify(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), default), Times.Once);
            backend.VerifyAll(); backend.VerifyNoOtherCalls();
        }
        finally { window.Close(); }
    });

    private static RatedSession Row()
    {
        var at = DateTime.SpecifyKind(DateTime.UnixEpoch.AddYears(55), DateTimeKind.Unspecified);
        return new(Guid.NewGuid(), "游戏", at, at.AddMinutes(20), 20,
            new SessionRating(4, "已有想法", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 7), 7);
    }
    private static async Task Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(412, 352)); content.Arrange(new Rect(0, 0, 412, 352));
        await Dispatcher.Yield(DispatcherPriority.DataBind);
    }
    private static Button[] Stars(Window window) => Descendants(window).OfType<Button>().Where(b => b.Tag is int n && n is >= 1 and <= 5).OrderBy(b => (int)b.Tag).ToArray();
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
    private static Task OnDispatcher(Func<Task> test)
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
