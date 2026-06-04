using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.Tests;

public sealed class StartupModeResolverTests
{
    [Fact]
    public void Resolve_NonFileDropAndNotClaimed_Exits()
    {
        var mode = StartupModeResolver.Resolve(isFileDropRequest: false, claimedSingleInstance: false);
        Assert.Equal(StartupMode.ExitAlreadyRunning, mode);
    }

    [Fact]
    public void Resolve_FileDropAndNotClaimed_ForwardsToRunningInstance()
    {
        var mode = StartupModeResolver.Resolve(isFileDropRequest: true, claimedSingleInstance: false);
        Assert.Equal(StartupMode.ForwardFileDropToRunningInstance, mode);
    }

    [Fact]
    public void Resolve_FileDropAndClaimed_HandlesLocally()
    {
        var mode = StartupModeResolver.Resolve(isFileDropRequest: true, claimedSingleInstance: true);
        Assert.Equal(StartupMode.HandleFileDropLocally, mode);
    }
}
