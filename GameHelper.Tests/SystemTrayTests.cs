using GameHelper.Core.Abstractions;
using GameHelper.Infrastructure.Notifications;
using Moq;

namespace GameHelper.Tests;

public sealed class SystemTrayTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "", true)]
    [InlineData(false, null, false)]
    [InlineData(true, "sandbox", false)]
    [InlineData(false, "sandbox", false)]
    public void TrayOnlyStartsInUnsandboxedBackgroundMode(bool background, string? dataDirectory, bool expected)
    {
        Assert.Equal(expected, SystemTrayLogic.ShouldStart(background, dataDirectory));
    }

    [Fact]
    public void SummaryIncludesMonitoringGameNamesAndSessionDurations()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var sessions = new Dictionary<string, DateTime?>
        {
            ["b"] = now.AddMinutes(-125), ["a"] = now.AddMinutes(-15), ["unknown"] = null, ["future"] = now.AddMinutes(5)
        };
        var summary = SystemTrayLogic.Summary(true, sessions, new Dictionary<string, string> { ["a"] = "游戏甲" }, now);
        Assert.Contains("监控中", summary);
        Assert.Contains("游戏甲（本局 15 分钟）", summary);
        Assert.Contains("b（本局 125 分钟）", summary);
        Assert.Contains("unknown（时长未知）", summary);
        Assert.Contains("future（本局 0 分钟）", summary);
        Assert.True(summary.IndexOf("游戏甲", StringComparison.Ordinal) < summary.IndexOf("b（", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "监控中")]
    [InlineData(false, "监控已停止")]
    public void EmptySummaryStillExplainsState(bool monitoring, string state)
    {
        var summary = SystemTrayLogic.Summary(monitoring, new Dictionary<string, DateTime?>(),
            new Dictionary<string, string>(), DateTime.MinValue);
        Assert.Contains(state, summary);
        Assert.Contains("暂无运行中的游戏", summary);
    }

    [Fact]
    public void TooltipFitsNativeBufferAndDoesNotSplitSurrogatePairs()
    {
        Assert.Equal("GameHelper 监控中", SystemTrayLogic.Tooltip("GameHelper\n监控中"));
        var tooltip = SystemTrayLogic.Tooltip(new string('游', 125) + "🎮" + new string('戏', 20));
        Assert.True(tooltip.Length <= 127);
        Assert.Equal(new string('游', 125) + "…", tooltip);
    }

    [Theory]
    [InlineData(1, 1, 0, 0, 0)]
    [InlineData(2, 0, 1, 0, 0)]
    [InlineData(3, 0, 0, 1, 0)]
    [InlineData(4, 0, 0, 0, 1)]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(99, 0, 0, 0, 0)]
    public void MenuDispatchesExactlyOneAction(uint command, int opens, int snoozes, int resets, int exits)
    {
        var reminders = new Mock<IBreakReminderService>(MockBehavior.Strict);
        reminders.Setup(service => service.Snooze(TimeSpan.FromMinutes(15)));
        reminders.Setup(service => service.ResetCycle());
        var openCount = 0;
        var exitCount = 0;
        Assert.Equal(command is >= 1 and <= 4,
            SystemTrayLogic.Dispatch(command, reminders.Object, () => openCount++, () => exitCount++));
        Assert.Equal(opens, openCount);
        Assert.Equal(exits, exitCount);
        reminders.Verify(service => service.Snooze(TimeSpan.FromMinutes(15)), Times.Exactly(snoozes));
        reminders.Verify(service => service.ResetCycle(), Times.Exactly(resets));
        reminders.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenLibraryUsesCurrentConfigAndPublishedDesktopLayout(bool besideHost)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameHelper published");
        var config = Path.Combine(directory, "configuration with spaces.yml");
        var sameDirectory = Path.Combine(directory, "GameHelper.Desktop.exe");
        var start = SystemTrayLogic.LibraryStartInfo(directory, config, path => besideHost && path == sameDirectory);
        Assert.Equal(besideHost ? sameDirectory : Path.Combine(directory, "desktop", "GameHelper.Desktop.exe"), start.FileName);
        Assert.Equal(new[] { "--config", config }, start.ArgumentList);
        Assert.True(start.UseShellExecute);
        Assert.Equal(Path.GetDirectoryName(start.FileName), start.WorkingDirectory);
    }
}
