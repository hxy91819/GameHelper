namespace GameHelper.ConsoleHost.Utilities;

internal enum StartupMode
{
    ContinueNormally,
    ExitAlreadyRunning,
    HandleFileDropLocally,
    ForwardFileDropToRunningInstance
}

internal static class StartupModeResolver
{
    public static StartupMode Resolve(bool isFileDropRequest, bool claimedSingleInstance)
    {
        if (claimedSingleInstance)
        {
            return isFileDropRequest ? StartupMode.HandleFileDropLocally : StartupMode.ContinueNormally;
        }

        return isFileDropRequest ? StartupMode.ForwardFileDropToRunningInstance : StartupMode.ExitAlreadyRunning;
    }

    /// <summary>
    /// Whether startup should hand off to the WPF library. Only a bare invocation (optionally with
    /// <c>--config/-c</c>) qualifies: a background runtime, debug logging, monitor overrides, dry-run,
    /// or any sub-command/dropped path must stay on the console path so the flag or command takes effect.
    /// </summary>
    public static bool ShouldOpenLibrary(ParsedArguments parsedArgs)
    {
        ArgumentNullException.ThrowIfNull(parsedArgs);

        return !parsedArgs.Background
            && !parsedArgs.EnableDebug
            && string.IsNullOrWhiteSpace(parsedArgs.MonitorType)
            && !parsedArgs.MonitorDryRun
            && parsedArgs.EffectiveArgs.Length == 0;
    }
}
