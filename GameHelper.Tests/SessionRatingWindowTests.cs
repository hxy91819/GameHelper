using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Desktop;
using Moq;

namespace GameHelper.Tests;

public sealed class SessionRatingWindowTests
{
    [Fact]
    public Task RealEditorBindings_CancelDoesNotWrite_AndFailedSaveKeepsVisibleFields() => OnDispatcher(async () =>
    {
        var backend = new Mock<IGameLibrary>(MockBehavior.Strict);
        var at = DateTime.SpecifyKind(DateTime.UnixEpoch.AddYears(55), DateTimeKind.Unspecified);
        var row = new RatedSession(Guid.NewGuid(), "游戏", at, at.AddMinutes(20), 20, null, 0);
        var cancelled = new SessionRatingWindow(backend.Object, row);
        cancelled.Close();
        backend.VerifyNoOtherCalls();

        var window = new SessionRatingWindow(backend.Object, row);
        try
        {
            var model = Assert.IsType<SessionRatingEditorViewModel>(window.DataContext);
            var grid = Assert.IsType<Grid>(window.Content);
            grid.Measure(new Size(440, 380)); grid.Arrange(new Rect(0, 0, 440, 380));
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var stars = Descendants(grid).OfType<ComboBox>().Single();
            var thought = Descendants(grid).OfType<TextBox>().Single();
            stars.SelectedItem = 5; thought.Text = "保留可见输入";
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(5, model.Stars); Assert.Equal("保留可见输入", model.Thought);
            backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), default))
                .ReturnsAsync(new GameLibraryResponse { Error = "保存失败" });
            Assert.False(await model.SaveAsync());
            Assert.Equal(5, stars.SelectedItem); Assert.Equal("保留可见输入", thought.Text);
            Assert.Equal("保存失败", model.Status);
        }
        finally { window.Close(); }
    });

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
