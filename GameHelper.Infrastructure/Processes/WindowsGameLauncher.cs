using System.Diagnostics;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes;

/// <summary>Uses the original executable, shortcut or Steam entry without wrapping it in a shell command.</summary>
public sealed class WindowsGameLauncher : IGameLauncher
{
    private readonly Action<string> _ensureCompanion;
    private readonly Action<GameLaunchOptions> _startEntry;
    private readonly Action _stopCompanion;
    public WindowsGameLauncher() : this(Ds4WindowsProcess.EnsureRunning, StartEntry, Ds4WindowsProcess.StopRunning) { }
    internal WindowsGameLauncher(Action<string> ensureCompanion, Action<GameLaunchOptions> startEntry, Action stopCompanion)
    { _ensureCompanion = ensureCompanion; _startEntry = startEntry; _stopCompanion = stopCompanion; }

    public void Start(GameLaunchOptions options)
    {
        Validate(options);
        if (options.Ds4WindowsPath is { } companion) EnsureCompanionRunning(companion);
        else StopCompanion();
        _startEntry(options);
    }

    public void ExecuteAction(GameAction action)
    {
        action.Validate();
        if (action.Type == "close") { Ds4WindowsProcess.StopRunning(action.Path); return; }
        if (!File.Exists(action.Path)) throw new ArgumentException($"动作程序不存在：{action.Path}");
        var start = Ds4WindowsProcess.CreateStartInfo(action.Path);
        start.Arguments = action.Arguments ?? string.Empty;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"未能启动：{action.Path}");
        if (action.Wait != true) return;
        if (!process.WaitForExit(30000)) throw new TimeoutException($"动作等待退出超过 30 秒：{action.Path}");
        if (process.ExitCode != 0) throw new InvalidOperationException($"动作退出码 {process.ExitCode}：{action.Path}");
    }

    public void StopCompanion() => _stopCompanion();

    public void EnsureCompanionRunning(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new ArgumentException("DS4Windows 程序不存在，请重新选择路径，或关闭启动前运行选项。");
        _ensureCompanion(executablePath);
    }

    private static void StartEntry(GameLaunchOptions options)
    {
        using var process = Process.Start(new ProcessStartInfo(options.Target)
        {
            Arguments = options.Arguments ?? string.Empty,
            WorkingDirectory = !string.IsNullOrWhiteSpace(options.WorkingDirectory) ? options.WorkingDirectory
                : (Path.IsPathFullyQualified(options.Target) ? Path.GetDirectoryName(options.Target) : null) ?? string.Empty,
            UseShellExecute = true
        });
    }

    public void Validate(GameLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.Ds4WindowsPath is { } companion && !File.Exists(companion))
            throw new ArgumentException("DS4Windows 程序不存在，请重新选择路径，或关闭启动前运行选项。");
        if (options.Target.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(options.Target)) throw new ArgumentException("游戏启动入口不存在，请重新选择程序或快捷方式。");
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) && !Directory.Exists(options.WorkingDirectory))
            throw new ArgumentException("启动工作目录不存在。");
    }
}
