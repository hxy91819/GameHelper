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
}
