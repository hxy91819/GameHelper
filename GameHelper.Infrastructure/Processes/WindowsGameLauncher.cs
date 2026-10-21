using System.Diagnostics;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes;

/// <summary>Uses the original executable, shortcut or Steam entry without wrapping it in a shell command.</summary>
public sealed class WindowsGameLauncher : IGameLauncher
{
    public void Start(GameLaunchOptions options)
    {
        Validate(options);
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
        if (options.Target.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(options.Target)) throw new ArgumentException("游戏启动入口不存在，请重新选择程序或快捷方式。");
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) && !Directory.Exists(options.WorkingDirectory))
            throw new ArgumentException("启动工作目录不存在。");
    }
}
