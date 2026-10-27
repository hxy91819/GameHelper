using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class WindowsHdrControllerTests
{
    [Fact]
    public void ManualPrimaryToggle_OnlyChangesPrimaryAndNeverAddsAutomationRollbackTargets()
    {
        var displays = new[]
        {
            new WindowsHdrController.DisplayAdvancedColorInfo("secondary", true, false, false, "second"),
            new WindowsHdrController.DisplayAdvancedColorInfo("primary", true, false, false, "main", Primary: true)
        };
        var writes = new List<string>();
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance, () => displays,
            (display, enabled) =>
            {
                writes.Add(display.DevicePath);
                displays = displays.Select(d => d.DevicePath == display.DevicePath ? d with { Enabled = enabled } : d).ToArray();
            }, () => { });
        controller.Configure(new() { AllowedDisplays = ["secondary"] });
        controller.TogglePrimary();
        Assert.True(controller.ReadPrimary().Enabled);
        Assert.False(displays[0].Enabled);
        Assert.Equal(new[] { "main" }, writes);
        controller.Enable();
        controller.Disable();
        Assert.True(controller.ReadPrimary().Enabled);
        Assert.False(displays[0].Enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnknownOrAmbiguousPrimary_CannotWrite(int count)
    {
        var displays = Enumerable.Range(0, count).Select(i =>
            new WindowsHdrController.DisplayAdvancedColorInfo(i.ToString(), true, false, false, Primary: true)).ToArray();
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance, () => displays,
            (_, _) => throw new Xunit.Sdk.XunitException("Must not write"), () => { });
        Assert.Null(controller.ReadPrimary().Enabled);
        Assert.False(controller.ReadPrimary().CanToggle);
        Assert.Throws<InvalidOperationException>(controller.TogglePrimary);
    }

    [Fact]
    public void PrimaryQueryFailureAndUnconfirmedWrite_ReportFailure()
    {
        var fail = false;
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => fail ? throw new Win32Exception(5) : [new("primary", true, false, false, Primary: true)],
            (_, _) => { }, () => { });
        Assert.Throws<InvalidOperationException>(controller.TogglePrimary);
        fail = true;
        Assert.Null(controller.ReadPrimary().Enabled);
        Assert.False(controller.ReadPrimary().CanToggle);
        Assert.Throws<InvalidOperationException>(controller.TogglePrimary);
    }

    [Theory]
    [InlineData("desk", false)]
    [InlineData("DESK", false)]
    [InlineData("desk-path", false)]
    [InlineData("other", true)]
    public void DisabledDisplay_ExactNameOrPathMatch_ExcludesOnlyMatchingTarget(string rule, bool expectedAllowed)
    {
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => new[] { new WindowsHdrController.DisplayAdvancedColorInfo("desk", true, false, false, "desk-path") });
        controller.Configure(new HdrSettings { DisabledDisplays = [rule] });
        Assert.Equal(expectedAllowed ? HdrAutomationAvailability.Available : HdrAutomationAvailability.ExcludedByPolicy,
            controller.GetAutomationAvailability());
        Assert.True(controller.TryGetDisplays(out var displays));
        Assert.Equal(expectedAllowed, Assert.Single(displays).Allowed);
        Assert.Equal("desk-path", displays[0].DevicePath);
    }

    [Fact]
    public void WhitelistAndExclusion_ExclusionWins_AndSystemStateIncludesExcludedDisplay()
    {
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => new[] { new WindowsHdrController.DisplayAdvancedColorInfo("TV", true, true, false, "tv-path") });
        controller.Configure(new HdrSettings { AllowedDisplays = ["TV"], DisabledDisplays = ["tv-path"] });
        Assert.Equal(HdrAutomationAvailability.ExcludedByPolicy, controller.GetAutomationAvailability());
        Assert.True(controller.TryGetEnabled(out var enabled));
        Assert.True(enabled);
    }

    [Fact]
    public void DisplaySelection_HotplugAndReload_QueriesFreshTargetsAndCopiesSettings()
    {
        var displays = new[] { new WindowsHdrController.DisplayAdvancedColorInfo("desk", true, false, false, "desk-path") };
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance, () => displays);
        var settings = new HdrSettings { AllowedDisplays = ["TV"] };
        controller.Configure(settings);
        settings.AllowedDisplays.Clear();
        Assert.Equal(HdrAutomationAvailability.ExcludedByPolicy, controller.GetAutomationAvailability());
        displays = [new("TV", true, false, false, "tv-path")];
        Assert.Equal(HdrAutomationAvailability.Available, controller.GetAutomationAvailability());
        controller.Configure(new HdrSettings { DisabledDisplays = ["TV"] });
        Assert.Equal(HdrAutomationAvailability.ExcludedByPolicy, controller.GetAutomationAvailability());
        controller.Configure(null);
        Assert.Equal(HdrAutomationAvailability.Available, controller.GetAutomationAvailability());
    }

    [Fact]
    public void Availability_UnsupportedForceDisabledOrFailedQuery_DoesNotClaimPolicySkip()
    {
        var fail = false;
        var displays = new[] { new WindowsHdrController.DisplayAdvancedColorInfo("TV", false, false, false, "tv-path") };
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => fail ? throw new Win32Exception(5) : displays);
        controller.Configure(new HdrSettings { AllowedDisplays = ["TV"] });
        Assert.Equal(HdrAutomationAvailability.Unavailable, controller.GetAutomationAvailability());
        displays = [new("TV", true, false, true, "tv-path")];
        Assert.Equal(HdrAutomationAvailability.Unavailable, controller.GetAutomationAvailability());
        fail = true;
        Assert.Equal(HdrAutomationAvailability.Unknown, controller.GetAutomationAvailability());
        Assert.False(controller.TryGetDisplays(out _));
    }

    [Fact]
    public void Availability_ExcludedHdrDesktopAndAllowedSdrTelevision_ReportsUnavailable()
    {
        // The allowed television is online but cannot do HDR; the excluded desktop is HDR-capable.
        // This is missing hardware, not a policy-only skip.
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => new[]
            {
                new WindowsHdrController.DisplayAdvancedColorInfo("desk", true, false, false, "desk-path"),
                new WindowsHdrController.DisplayAdvancedColorInfo("TV", false, false, false, "tv-path")
            });
        controller.Configure(new HdrSettings { AllowedDisplays = ["tv-path"] });
        Assert.Equal(HdrAutomationAvailability.Unavailable, controller.GetAutomationAvailability());
    }

    [Fact]
    public void Enable_TelevisionAndExcludedDesktop_OnlyWritesTelevisionAndRestoresAfterRuleReload()
    {
        var displays = new[]
        {
            new WindowsHdrController.DisplayAdvancedColorInfo("desk", true, false, false, "desk-path"),
            new WindowsHdrController.DisplayAdvancedColorInfo("TV", true, false, false, "tv-path")
        };
        var writes = new List<(string, bool)>();
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance, () => displays,
            (display, enabled) =>
            {
                writes.Add((display.Name, enabled));
                var index = Array.FindIndex(displays, d => d.DevicePath == display.DevicePath);
                displays[index] = display with { Enabled = enabled };
            }, () => { });
        controller.Configure(new HdrSettings { AllowedDisplays = ["TV"] });
        controller.Enable();
        Assert.Equal(new[] { ("TV", true) }, writes);
        Assert.True(controller.TryGetEnabled(out var enabled));
        Assert.True(enabled);
        controller.Configure(new HdrSettings { DisabledDisplays = ["TV", "desk"] });
        controller.Disable();
        Assert.Equal(new[] { ("TV", true), ("TV", false) }, writes);
        Assert.False(displays[0].Enabled);
    }

    [Fact]
    public void Enable_TargetWriteNotConfirmed_ThrowsEvenWhenExcludedScreenIsHdr()
    {
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => new[] { new WindowsHdrController.DisplayAdvancedColorInfo("desk", true, true, false, "desk-path"),
                new WindowsHdrController.DisplayAdvancedColorInfo("TV", true, false, false, "tv-path") },
            (_, _) => { }, () => { });
        controller.Configure(new HdrSettings { AllowedDisplays = ["TV"] });
        Assert.Throws<InvalidOperationException>(() => controller.Enable());
    }

    [Fact]
    public void DisplayConfigModeBuffer_MatchesNativeElementSize()
    {
        // The OS writes this ABI size, independently of which union members we inspect.
        var modeType = typeof(WindowsHdrController).GetNestedType("DISPLAYCONFIG_MODE_INFO", BindingFlags.NonPublic)!;
        Assert.Equal(64, Marshal.SizeOf(modeType));
    }

    [Fact]
    public void TryGetEnabled_ExternalStateChanges_QueriesCurrentStateInsteadOfCache()
    {
        var enabled = false;
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => new[] { new WindowsHdrController.DisplayAdvancedColorInfo("test", true, enabled, false) });
        Assert.False(controller.IsEnabled);
        enabled = true;
        Assert.True(controller.TryGetEnabled(out var current));
        Assert.True(current);
        enabled = false;
        Assert.True(controller.TryGetEnabled(out current));
        Assert.False(current);
    }

    [Fact]
    public void TryGetEnabled_QueryFails_ReportsUnknownRatherThanConfirmedSdr()
    {
        var fail = false;
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => fail ? throw new Win32Exception(5) : Array.Empty<WindowsHdrController.DisplayAdvancedColorInfo>());
        fail = true;
        Assert.False(controller.TryGetEnabled(out _));
        fail = false;
        Assert.True(controller.TryGetEnabled(out var current));
        Assert.False(current);
    }

    [Fact]
    public void TryGetEnabled_QueryFails_PreservesLastKnownHdrForOwnership()
    {
        var fail = false;
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => fail ? throw new Win32Exception(5)
                : new[] { new WindowsHdrController.DisplayAdvancedColorInfo("test", true, true, false) });
        Assert.True(controller.IsEnabled);
        fail = true;
        Assert.False(controller.TryGetEnabled(out _));
        Assert.True(controller.IsEnabled); // Existing HDR ownership must not see a false SDR transition.
        fail = false;
        Assert.True(controller.TryGetEnabled(out var current));
        Assert.True(current);
    }
}
