using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.Tests;

public sealed class StartupModeResolverTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void ProcessInstanceGuard_WhenDisableEnvironmentVariableIsTruthy_DisablesSingleInstance(string value)
    {
        var previousValue = Environment.GetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE");
        try
        {
            Environment.SetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE", value);

            Assert.True(ProcessInstanceGuard.IsSingleInstanceDisabledByEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE", previousValue);
        }
    }

    [Fact]
    public void ProcessInstanceGuard_WhenDisableEnvironmentVariableIsUnset_DoesNotDisableSingleInstance()
    {
        var previousValue = Environment.GetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE");
        try
        {
            Environment.SetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE", null);

            Assert.False(ProcessInstanceGuard.IsSingleInstanceDisabledByEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE", previousValue);
        }
    }

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
