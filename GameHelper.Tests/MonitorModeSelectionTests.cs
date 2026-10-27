using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Models;

namespace GameHelper.Tests;

public sealed class MonitorModeSelectionTests
{
    [Fact]
    public void Resolve_WhenDryRun_IgnoresConfiguredMonitorType()
    {
        var selection = MonitorModeSelection.Resolve(
            new ParsedArguments { MonitorDryRun = true },
            ProcessMonitorType.WMI);

        Assert.True(selection.IsDryRun);
        Assert.Equal(ProcessMonitorType.ETW, selection.MonitorType);
        Assert.Equal(MonitorModeSource.DryRun, selection.Source);
    }

    [Fact]
    public void Resolve_WhenCommandLineProvided_UsesCommandLine()
    {
        var selection = MonitorModeSelection.Resolve(
            new ParsedArguments { MonitorType = "wmi" },
            ProcessMonitorType.ETW);

        Assert.Equal(MonitorModeSource.CommandLine, selection.Source);
        Assert.Equal(ProcessMonitorType.WMI, selection.MonitorType);
    }

    [Fact]
    public void Resolve_WhenCommandLineInvalid_UsesDefaultEtw()
    {
        var selection = MonitorModeSelection.Resolve(
            new ParsedArguments { MonitorType = "bad" },
            ProcessMonitorType.WMI);

        Assert.True(selection.HasInvalidCommandLineType);
        Assert.Equal(ProcessMonitorType.ETW, selection.MonitorType);
        Assert.Equal(MonitorModeSource.InvalidCommandLine, selection.Source);
    }

    [Fact]
    public void Resolve_WhenConfigured_UsesConfig()
    {
        var selection = MonitorModeSelection.Resolve(new ParsedArguments(), ProcessMonitorType.WMI);

        Assert.Equal(MonitorModeSource.Config, selection.Source);
        Assert.Equal(ProcessMonitorType.WMI, selection.MonitorType);
    }

    [Fact]
    public void Resolve_WhenNoInputs_UsesEtwDefault()
    {
        var selection = MonitorModeSelection.Resolve(new ParsedArguments(), configuredMonitorType: null);

        Assert.Equal(MonitorModeSource.Default, selection.Source);
        Assert.Equal(ProcessMonitorType.ETW, selection.MonitorType);
    }
}
