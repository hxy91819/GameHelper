using GameHelper.Infrastructure.Upload;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests.Sync;

public class ProcessGitRunnerTests
{
    [Fact]
    public async Task RunAsync_WithRealGit_ReturnsVersionOutput()
    {
        var runner = new ProcessGitRunner();

        var result = await runner.RunAsync(null, new[] { "--version" });

        Assert.True(result.Succeeded, result.DescribeError());
        Assert.Contains("git version", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_DisablesInteractiveCredentialPrompts()
    {
        // 用 cmd 读取子进程环境，验证快速失败所需的环境变量确实被注入。
        var runner = new ProcessGitRunner(
            NullLogger<ProcessGitRunner>.Instance,
            timeoutSeconds: ProcessGitRunner.DefaultTimeoutSeconds,
            executableOverride: "cmd.exe");

        var prompt = await runner.RunAsync(null, new[] { "/c", "set GIT_TERMINAL_PROMPT" });
        Assert.True(prompt.Succeeded, prompt.DescribeError());
        Assert.Contains("GIT_TERMINAL_PROMPT=0", prompt.StandardOutput);

        var gcm = await runner.RunAsync(null, new[] { "/c", "set GCM_INTERACTIVE" });
        Assert.True(gcm.Succeeded, gcm.DescribeError());
        Assert.Contains("GCM_INTERACTIVE=never", gcm.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_WhenCommandExceedsTimeout_KillsProcessAndThrows()
    {
        var runner = new ProcessGitRunner(
            NullLogger<ProcessGitRunner>.Instance,
            timeoutSeconds: 1,
            executableOverride: "cmd.exe");

        await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(null, new[] { "/c", "ping -n 30 127.0.0.1 > nul" }));
    }
}
