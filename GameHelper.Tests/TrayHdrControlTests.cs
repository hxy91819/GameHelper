using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class TrayHdrControlTests
{
    [Fact]
    public void PendingLease_DisablesManualChangesAndReleaseReenablesThem()
    {
        var hdr = new Display();
        var scheduler = new HdrScheduler();
        var tray = new TrayHdrControl(hdr);
        Assert.True(tray.Read().CanToggle);
        scheduler.PrepareForLaunch(hdr, NullLogger.Instance);
        Assert.True(hdr.Enabled);
        Assert.False(tray.Read().CanToggle);
        Assert.Contains("租约", tray.Read().Reason);
        Assert.Throws<InvalidOperationException>(tray.Toggle);
        scheduler.Update([], new Dictionary<string, GameConfig>(), hdr, NullLogger.Instance);
        Assert.False(hdr.Enabled);
        tray.Toggle();
        Assert.True(hdr.Enabled);
    }

    [Fact]
    public void InitiallyEnabledHdr_DoesNotGrantLeaseAndManualChangesAreNotRolledBack()
    {
        var hdr = new Display { Enabled = true };
        var scheduler = new HdrScheduler();
        scheduler.PrepareForLaunch(hdr, NullLogger.Instance);
        var tray = new TrayHdrControl(hdr);
        Assert.True(tray.Read().CanToggle);
        scheduler.Update([], new Dictionary<string, GameConfig>(), hdr, NullLogger.Instance);
        Assert.True(hdr.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredLease_AndFailedRollbackRemainProtected(bool failRollback)
    {
        var hdr = new Display { Enabled = true, FailDisable = failRollback };
        var scheduler = new HdrScheduler();
        scheduler.AdoptOwnership(true);
        var games = new Dictionary<string, GameConfig> { ["a"] = new() { HdrEnabled = true } };
        scheduler.Update(["a"], games, hdr, NullLogger.Instance);
        var tray = new TrayHdrControl(hdr);
        Assert.False(tray.Read().CanToggle);
        if (failRollback)
        {
            Assert.Throws<InvalidOperationException>(() => scheduler.Update([], games, hdr, NullLogger.Instance));
            Assert.False(tray.Read().CanToggle);
        }
        else
        {
            scheduler.Update([], games, hdr, NullLogger.Instance);
            Assert.True(tray.Read().CanToggle);
        }
    }

    [Fact]
    public void SeparateControllers_DoNotShareLeases()
    {
        var first = new Display();
        var second = new Display();
        new HdrScheduler().PrepareForLaunch(first, NullLogger.Instance);
        Assert.True(new TrayHdrControl(second).Read().CanToggle);
    }

    private sealed class Display : IHdrController, IPrimaryDisplayHdrController
    {
        public bool Enabled { get; set; }
        public bool FailDisable { get; set; }
        public bool IsEnabled => Enabled;
        public void Enable() => Enabled = true;
        public void Disable()
        {
            if (FailDisable) throw new InvalidOperationException("write failed");
            Enabled = false;
        }
        public TrayHdrState ReadPrimary() => new(Enabled, true);
        public void TogglePrimary() => Enabled = !Enabled;
    }
}
