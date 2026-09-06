using GameHelper.Infrastructure.Processes;

namespace GameHelper.Tests;

/// <summary>
/// 回归：陈旧 ETW 会话清理必须区分"属主已死的残留"与"活跃实例的会话"。
/// 此前按前缀无差别清理，另一实例启动会杀掉运行中实例的活跃会话，
/// 导致其静默失聪、游玩时长丢失（会话名现已内嵌属主 PID）。
/// </summary>
public class EtwProcessMonitorStaleSessionTests
{
    private const string Prefix = "GameHelper-ETW-";

    [Fact]
    public void ShouldCleanupSession_LiveOwnerPid_IsKept()
    {
        var sessionName = $"{Prefix}{Environment.ProcessId}-0123abcd";
        Assert.False(EtwProcessMonitor.ShouldCleanupSession(sessionName, pid => pid == Environment.ProcessId));
    }

    [Fact]
    public void ShouldCleanupSession_DeadOwnerPid_IsCleaned()
    {
        var sessionName = $"{Prefix}999999-0123abcd";
        Assert.True(EtwProcessMonitor.ShouldCleanupSession(sessionName, pid => pid != 999999));
    }

    [Fact]
    public void ShouldCleanupSession_LegacyFormatWithoutPid_IsCleaned()
    {
        // 旧版本会话名无 PID，无法判定属主，保持既有清理行为。
        var sessionName = $"{Prefix}0123abcd5678ef90";
        Assert.True(EtwProcessMonitor.ShouldCleanupSession(sessionName, _ => true));
    }

    [Fact]
    public void ShouldCleanupSession_NonGameHelperName_IsIgnored()
    {
        // 非 GameHelper 前缀的会话（系统/其他软件的）永不触碰。
        Assert.False(EtwProcessMonitor.ShouldCleanupSession("NT Kernel Logger", _ => false));
        Assert.False(EtwProcessMonitor.ShouldCleanupSession("GameHelperLog", _ => false));
    }

    [Fact]
    public void ShouldCleanupSession_MalformedPid_IsCleaned()
    {
        var sessionName = $"{Prefix}notapid-0123abcd";
        Assert.True(EtwProcessMonitor.ShouldCleanupSession(sessionName, _ => true));
    }
}
