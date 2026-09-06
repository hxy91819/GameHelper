using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Infrastructure.Upload;

/// <summary>
/// 通过 git.exe 执行命令。禁用交互式凭据提示（GIT_TERMINAL_PROMPT=0、GCM_INTERACTIVE=never），
/// 凭据未就绪时快速失败而不是挂起后台任务；单条命令超时后强制结束进程树。
/// </summary>
public sealed class ProcessGitRunner : IGitRunner
{
    /// <summary>单条 git 命令的超时（秒）。</summary>
    public const int DefaultTimeoutSeconds = 120;

    private readonly int _timeoutSeconds;
    private readonly ILogger<ProcessGitRunner> _logger;
    private readonly string _executable;

    public ProcessGitRunner(ILogger<ProcessGitRunner>? logger = null, int timeoutSeconds = DefaultTimeoutSeconds)
        : this(logger, timeoutSeconds, executableOverride: null)
    {
    }

    /// <summary>测试专用构造：允许替换可执行文件以验证超时与环境注入行为。</summary>
    internal ProcessGitRunner(
        ILogger<ProcessGitRunner>? logger,
        int timeoutSeconds,
        string? executableOverride)
    {
        _logger = logger ?? NullLogger<ProcessGitRunner>.Instance;
        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        _timeoutSeconds = timeoutSeconds;
        _executable = string.IsNullOrWhiteSpace(executableOverride) ? "git" : executableOverride!;
    }

    public async Task<GitRunResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 凭据未就绪时快速失败，绝不弹出交互式提示挂起后台循环。
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "未找到 git.exe。请安装 Git for Windows 并确认 git 在 PATH 中；或在 config.yml 改用 sync.method: api。",
                ex);
        }

        _logger.LogDebug("git {Arguments}", string.Join(' ', arguments));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SafeKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                $"git 命令超时（{_timeoutSeconds}s）：{_executable} {string.Join(' ', arguments)}");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new GitRunResult(process.ExitCode, stdout, stderr);
    }

    private static void SafeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已自行退出；单点失败不阻断后续清理。
        }

        try
        {
            process.WaitForExit(5000);
        }
        catch
        {
            // 同上。
        }
    }
}
