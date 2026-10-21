using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using GameHelper.Infrastructure.Controllers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class WindowsHdrControllerTests
{
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
