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
        Assert.Equal("Dry-run（仅演练）", selection.GetDisplayText());
    }

    [Fact]
    public void Resolve_WhenCommandLineProvided_UsesCommandLine()
    {
        var selection = MonitorModeSelection.Resolve(
            new ParsedArguments { MonitorType = "wmi" },
            ProcessMonitorType.ETW);

        Assert.Equal(MonitorModeSource.CommandLine, selection.Source);
        Assert.Equal(ProcessMonitorType.WMI, selection.MonitorType);
        Assert.Equal("WMI（命令行指定）", selection.GetDisplayText());
    }

    [Fact]
    public void Resolve_WhenCommandLineInvalid_UsesDefaultEtw()
    {
        var selection = MonitorModeSelection.Resolve(
            new ParsedArguments { MonitorType = "bad" },
            ProcessMonitorType.WMI);

        Assert.True(selection.HasInvalidCommandLineType);
        Assert.Equal(ProcessMonitorType.ETW, selection.MonitorType);
        Assert.Equal("ETW（命令行无效，使用默认）", selection.GetDisplayText());
    }

    [Fact]
    public void Resolve_WhenConfigured_UsesConfig()
    {
        var selection = MonitorModeSelection.Resolve(new ParsedArguments(), ProcessMonitorType.WMI);

        Assert.Equal(MonitorModeSource.Config, selection.Source);
        Assert.Equal(ProcessMonitorType.WMI, selection.MonitorType);
        Assert.Equal("WMI（配置文件）", selection.GetDisplayText());
    }

    [Fact]
    public void Resolve_WhenNoInputs_UsesEtwDefault()
    {
        var selection = MonitorModeSelection.Resolve(new ParsedArguments(), configuredMonitorType: null);

        Assert.Equal(MonitorModeSource.Default, selection.Source);
        Assert.Equal(ProcessMonitorType.ETW, selection.MonitorType);
        Assert.Equal("ETW（默认）", selection.GetDisplayText());
    }
}
