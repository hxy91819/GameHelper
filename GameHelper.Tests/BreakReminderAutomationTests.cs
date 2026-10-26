using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class BreakReminderAutomationTests
{
    [Fact]
    public void AutomationLifecycle_StartReloadStop_DrivesRemindersWithoutOtherSideEffects()
    {
        var configuration = new AppConfig { BreakReminder = new BreakReminderSettings { Enabled = true } };
        var provider = new Mock<IGameConfiguration>();
        provider.Setup(source => source.Read()).Returns(configuration);
        var reminders = new Mock<IBreakReminderService>();
        var playtime = new Mock<IPlayTimeService>(MockBehavior.Strict);
        var hdr = new Mock<IHdrController>(MockBehavior.Strict);
        hdr.Setup(controller => controller.Configure(It.IsAny<HdrSettings?>()));
        var speed = new Mock<IGameSpeedController>(MockBehavior.Strict);
        var automation = new GameAutomationService(new Mock<IProcessMonitor>().Object, provider.Object,
            hdr.Object, playtime.Object, NullLogger<GameAutomationService>.Instance,
            speedController: speed.Object, breakReminder: reminders.Object);
        automation.Start();
        automation.Start();
        automation.ReloadConfig();
        automation.Stop();
        reminders.Verify(service => service.Start(configuration), Times.Once);
        reminders.Verify(service => service.ReloadConfig(configuration), Times.Once);
        reminders.Verify(service => service.Stop(), Times.Once);
        playtime.VerifyNoOtherCalls();
        hdr.Verify(controller => controller.Configure(It.IsAny<HdrSettings?>()), Times.Exactly(2));
        hdr.VerifyNoOtherCalls();
        speed.VerifyNoOtherCalls();
    }

    [Fact]
    public void ProcessMonitorStartFailure_RollsBackReminderLifecycle()
    {
        var monitor = new Mock<IProcessMonitor>();
        monitor.Setup(service => service.Start()).Throws<InvalidOperationException>();
        var provider = new Mock<IGameConfiguration>();
        provider.Setup(service => service.Read()).Returns(new AppConfig());
        var reminders = new Mock<IBreakReminderService>();
        var automation = new GameAutomationService(monitor.Object, provider.Object,
            new Mock<IHdrController>().Object, new Mock<IPlayTimeService>().Object,
            NullLogger<GameAutomationService>.Instance, breakReminder: reminders.Object);
        var lifecycle = new MonitorControlService(monitor.Object, automation);
        Assert.Throws<InvalidOperationException>(() => lifecycle.Start());
        Assert.False(lifecycle.IsRunning);
        reminders.Verify(service => service.Stop(), Times.Once);
    }
}
