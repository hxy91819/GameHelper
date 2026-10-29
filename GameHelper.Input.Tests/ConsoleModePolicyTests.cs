using GameHelper.Desktop;
using Xunit;
namespace GameHelper.Input.Tests;
public class ConsoleModePolicyTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(100); }
    [Fact] public void TabsFilterAndSortWithoutDesktopFilters()
    {
        var clock = new Clock(); var now = clock.GetUtcNow();
        ConsoleModePolicy.Entry[] games = [new("old",now.AddDays(-31),true,"full",null), new("new",now,false,"partial",null), new("manual",null,false,"unlisted",true)];
        Assert.Equal(new[]{"new","old","manual"},ConsoleModePolicy.Select(games,0,clock).Select(e=>e.Key));
        Assert.Equal(new[]{"new"},ConsoleModePolicy.Select(games,1,clock).Select(e=>e.Key));
        Assert.Equal(new[]{"new","manual"},ConsoleModePolicy.Select(games,2,clock).Select(e=>e.Key));
        Assert.Equal(new[]{"old","manual"},ConsoleModePolicy.Select(games,3,clock).Select(e=>e.Key));
    }
    [Theory]
    [InlineData("partial",null,true)] [InlineData("unlisted",null,true)] [InlineData("unknown",null,false)]
    [InlineData("full",null,false)] [InlineData("full",false,true)] [InlineData("partial",true,false)]
    public void LaunchWarningRespectsManualOverride(string support,bool? manual,bool expected) => Assert.Equal(expected,ConsoleModePolicy.Confirm(support,manual));
    [Theory]
    [InlineData(true,true,false,true)] [InlineData(false,true,false,false)] [InlineData(true,false,false,false)] [InlineData(true,true,true,false)]
    public void RestoreOnlyAfterOurGameWasObservedRunning(bool own,bool seen,bool running,bool expected) => Assert.Equal(expected,ConsoleModePolicy.Restore(own,seen,running));
}
