using GameHelper.Core.Models;

namespace GameHelper.ConsoleHost.Utilities;

internal enum MonitorModeSource
{
    DryRun,
    CommandLine,
    Config,
    Default,
    InvalidCommandLine
}

internal sealed class MonitorModeSelection
{
    private MonitorModeSelection(ProcessMonitorType monitorType, MonitorModeSource source, string? requestedMonitorType)
    {
        MonitorType = monitorType;
        Source = source;
        RequestedMonitorType = requestedMonitorType;
    }

    public ProcessMonitorType MonitorType { get; }

    public MonitorModeSource Source { get; }

    public string? RequestedMonitorType { get; }

    public bool IsDryRun => Source == MonitorModeSource.DryRun;

    public bool HasInvalidCommandLineType => Source == MonitorModeSource.InvalidCommandLine;

    public static MonitorModeSelection Resolve(ParsedArguments arguments, ProcessMonitorType? configuredMonitorType)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.MonitorDryRun)
        {
            return new MonitorModeSelection(ProcessMonitorType.ETW, MonitorModeSource.DryRun, arguments.MonitorType);
        }

        if (!string.IsNullOrWhiteSpace(arguments.MonitorType))
        {
            return Enum.TryParse<ProcessMonitorType>(arguments.MonitorType, true, out var commandLineMonitorType)
                ? new MonitorModeSelection(commandLineMonitorType, MonitorModeSource.CommandLine, arguments.MonitorType)
                : new MonitorModeSelection(ProcessMonitorType.ETW, MonitorModeSource.InvalidCommandLine, arguments.MonitorType);
        }

        return configuredMonitorType.HasValue
            ? new MonitorModeSelection(configuredMonitorType.Value, MonitorModeSource.Config, null)
            : new MonitorModeSelection(ProcessMonitorType.ETW, MonitorModeSource.Default, null);
    }

    public string GetDisplayText()
    {
        return Source switch
        {
            MonitorModeSource.DryRun => "Dry-run (rehearsal only)",
            MonitorModeSource.CommandLine => $"{MonitorType} (from command line)",
            MonitorModeSource.Config => $"{MonitorType} (from config)",
            MonitorModeSource.InvalidCommandLine => $"{MonitorType} (invalid command line, using default)",
            _ => $"{MonitorType} (default)"
        };
    }
}
