using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Moq;

namespace GameHelper.Tests;

public sealed class TrayMenuTests
{
    private static readonly DateTime Now = new(2026, 2, 3, 12, 0, 0);

    [Fact]
    public void ActiveRows_AreReadOnlyAndClampDurations()
    {
        var items = TrayMenuService.Build([
            Game("a", "运行中", Now.AddMinutes(-15)), Game("b", "运行中", Now.AddMinutes(2)),
            Game("c", "运行中·外部"), Game("d")], new(false, true), false, null, Now);
        Assert.Equal(new[] { "a · 本局 15 分钟", "b · 本局 0 分钟", "c · 本局时长未知" }, items.Take(3).Select(i => i.Text));
        Assert.All(items.Take(3), i => { Assert.False(i.Enabled); Assert.Null(i.Action); });
    }

    [Theory]
    [InlineData(true, true, null, "已开启")]
    [InlineData(false, true, null, "已关闭")]
    [InlineData(null, false, "无法读取", "状态未知")]
    [InlineData(true, false, "游戏持有 HDR 租约", "已开启")]
    public void HdrRow_ReflectsStateAndDisabledReason(bool? enabled, bool canToggle, string? reason, string text)
    {
        var item = Assert.Single(TrayMenuService.Build([], new(enabled, canToggle, reason), false, null, Now), i => i.Action == TrayMenuAction.ToggleHdr);
        Assert.Equal(canToggle, item.Enabled);
        Assert.Contains(text, item.Text);
        if (reason is not null) Assert.Contains(reason, item.Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReminderRows_HideWhenDisabledAndEnableResumeOnlyWhenPaused(bool enabled, bool paused)
    {
        var rows = TrayMenuService.Build([], new(false, true), enabled, paused ? DateTimeOffset.UnixEpoch.AddDays(1) : null, Now);
        var reminderRows = rows.Where(i => i.Action is TrayMenuAction.Pause30Minutes or TrayMenuAction.PauseUntilTomorrow or TrayMenuAction.ResumeReminders).ToList();
        Assert.Equal(enabled ? 3 : 0, reminderRows.Count);
        if (enabled) Assert.Equal(paused, reminderRows.Single(i => i.Action == TrayMenuAction.ResumeReminders).Enabled);
        Assert.DoesNotContain(rows, i => i.Text.Contains("本局"));
    }

    [Fact]
    public void RecentGames_AreFiveMostRecentlyPlayedAndRunningOrPendingCannotLaunch()
    {
        var games = Enumerable.Range(0, 7).Select(i => Game(i.ToString(), i == 6 ? "运行中" : i == 5 ? "启动中" : "未运行")
            with { LastPlayedAt = DateTimeOffset.UnixEpoch.AddDays(i) }).Append(Game("never")).ToArray();
        var recent = TrayMenuService.Build(games, new(false, true), false, null, Now).Where(i => i.Action == TrayMenuAction.LaunchGame).ToList();
        Assert.Equal(new[] { "6", "5", "4", "3", "2" }, recent.Select(i => i.DataKey));
        Assert.All(recent.Take(2), i => Assert.False(i.Enabled));
        Assert.All(recent.Skip(2), i => Assert.True(i.Enabled));
    }

    [Theory]
    [InlineData(TrayMenuAction.OpenLibrary)]
    [InlineData(TrayMenuAction.Exit)]
    [InlineData(TrayMenuAction.ToggleHdr)]
    [InlineData(TrayMenuAction.Pause30Minutes)]
    [InlineData(TrayMenuAction.PauseUntilTomorrow)]
    [InlineData(TrayMenuAction.ResumeReminders)]
    public async Task Actions_DispatchExactlyOnce(TrayMenuAction action)
    {
        var reminders = new Mock<IBreakReminderService>(MockBehavior.Strict);
        var hdr = new Mock<ITrayHdrControl>(MockBehavior.Strict);
        var clock = new FixedClock();
        var expected = action == TrayMenuAction.Pause30Minutes ? clock.GetUtcNow().AddMinutes(30) : new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.FromHours(8));
        if (action is TrayMenuAction.Pause30Minutes or TrayMenuAction.PauseUntilTomorrow) reminders.Setup(r => r.PauseUntil(expected));
        if (action == TrayMenuAction.ResumeReminders) reminders.Setup(r => r.Resume());
        if (action == TrayMenuAction.ToggleHdr) hdr.Setup(h => h.Toggle());
        var opens = 0;
        var exits = 0;
        var service = new TrayMenuService(Mock.Of<IGameLibrary>(), hdr.Object, reminders.Object, clock, () => opens++, () => exits++);
        await service.DispatchAsync(new("action", action, true));
        Assert.Equal(action == TrayMenuAction.OpenLibrary ? 1 : 0, opens);
        Assert.Equal(action == TrayMenuAction.Exit ? 1 : 0, exits);
        reminders.VerifyAll(); hdr.VerifyAll();
        reminders.VerifyNoOtherCalls(); hdr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Launch_UsesLibraryCommandAndRechecksRunningState()
    {
        var library = new Mock<IGameLibrary>(MockBehavior.Strict);
        var game = Game("a");
        library.Setup(l => l.ExecuteAsync(It.Is<GameLibraryRequest>(r => r.Operation == GameLibraryOperation.List), default))
            .ReturnsAsync(() => new GameLibraryResponse { Success = true, Games = [game] });
        library.Setup(l => l.ExecuteAsync(It.Is<GameLibraryRequest>(r => r.Operation == GameLibraryOperation.Launch && r.DataKey == "a"), default))
            .ReturnsAsync(new GameLibraryResponse { Success = true });
        var service = new TrayMenuService(library.Object, Mock.Of<ITrayHdrControl>(), Mock.Of<IBreakReminderService>(), new FixedClock(), () => { }, () => { });
        var item = new TrayMenuItem("a", TrayMenuAction.LaunchGame, true, "a");
        await service.DispatchAsync(item);
        game = game with { State = "运行中" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DispatchAsync(item));
        library.Verify(l => l.ExecuteAsync(It.Is<GameLibraryRequest>(r => r.Operation == GameLibraryOperation.Launch), default), Times.Once);
        await service.DispatchAsync(item with { Enabled = false });
        library.Verify(l => l.ExecuteAsync(It.Is<GameLibraryRequest>(r => r.Operation == GameLibraryOperation.List), default), Times.Exactly(2));
    }

    [Fact]
    public async Task SnapshotFailure_IsReportedToCaller()
    {
        var library = new Mock<IGameLibrary>();
        library.Setup(l => l.ExecuteAsync(It.IsAny<GameLibraryRequest>(), default)).ReturnsAsync(new GameLibraryResponse { Error = "历史读取失败" });
        var service = new TrayMenuService(library.Object, Mock.Of<ITrayHdrControl>(), Mock.Of<IBreakReminderService>(), new FixedClock(), () => { }, () => { });
        Assert.Equal("历史读取失败", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMenuAsync())).Message);
    }

    [Fact]
    public async Task EveryMenuOpening_QueriesFreshLibraryAndHdrState()
    {
        var library = new Mock<IGameLibrary>();
        var games = new[] { Game("first", "运行中", Now) };
        library.Setup(l => l.ExecuteAsync(It.Is<GameLibraryRequest>(r => r.Operation == GameLibraryOperation.List), default))
            .ReturnsAsync(() => new GameLibraryResponse { Success = true, Games = games });
        var hdr = new Mock<ITrayHdrControl>();
        hdr.SetupSequence(h => h.Read()).Returns(new TrayHdrState(false, true)).Returns(new TrayHdrState(true, false, "租约"));
        var service = new TrayMenuService(library.Object, hdr.Object, Mock.Of<IBreakReminderService>(), new FixedClock(), () => { }, () => { });
        Assert.Contains(await service.GetMenuAsync(), i => i.Text.StartsWith("first ·"));
        games = [];
        var second = await service.GetMenuAsync();
        Assert.DoesNotContain(second, i => i.Text.Contains("本局"));
        Assert.False(second.Single(i => i.Action == TrayMenuAction.ToggleHdr).Enabled);
        library.Verify(l => l.ExecuteAsync(It.IsAny<GameLibraryRequest>(), default), Times.Exactly(2));
    }

    [Fact]
    public async Task CanceledDispatch_DoesNotLaunchAfterAnAwaitedStateRead()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<GameLibraryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var library = new Mock<IGameLibrary>(MockBehavior.Strict);
        library.Setup(l => l.ExecuteAsync(It.Is<GameLibraryRequest>(r => r.Operation == GameLibraryOperation.List), cancellation.Token)).Returns(pending.Task);
        var service = new TrayMenuService(library.Object, Mock.Of<ITrayHdrControl>(), Mock.Of<IBreakReminderService>(), new FixedClock(), () => { }, () => { });
        var dispatch = service.DispatchAsync(new("a", TrayMenuAction.LaunchGame, true, "a"), cancellation.Token);
        cancellation.Cancel();
        pending.SetResult(new() { Success = true, Games = [Game("a")] });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        library.Verify(l => l.ExecuteAsync(It.IsAny<GameLibraryRequest>(), cancellation.Token), Times.Once);
    }

    private static LibraryGame Game(string key, string state = "未运行", DateTime? start = null) => new()
    { Config = new() { DataKey = key, DisplayName = key }, State = state, RunningStartedAt = start };

    private sealed class FixedClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test");
        public override DateTimeOffset GetUtcNow() => new(2026, 2, 3, 12, 0, 0, TimeSpan.Zero);
    }
}
