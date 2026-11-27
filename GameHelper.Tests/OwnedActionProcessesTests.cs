using System.Diagnostics;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;

namespace GameHelper.Tests;

public sealed class OwnedActionProcessesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-owned-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _pids = [];
    private OwnedActionProcesses Manager => new(Path.Combine(_root, "registry"));
    public OwnedActionProcessesTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ReuseCanEnableExitCleanup_ButCannotRevokeAnExistingPromise()
    {
        var action = Script("sticky");
        Manager.Run("game", action);
        var sticky = Ready("sticky");
        Manager.Run("game", action with { StopOnGameExit = null });
        Manager.EndGame("game");
        Assert.True(SpinWait.SpinUntil(() => Dead(sticky), TimeSpan.FromSeconds(10)));

        var enabled = Script("enabled");
        Manager.Run("other", enabled with { StopOnGameExit = null });
        var newlyManaged = Ready("enabled");
        Manager.Run("other", enabled);
        Manager.EndGame("other");
        Assert.True(SpinWait.SpinUntil(() => Dead(newlyManaged), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void RestartReusesOwnedTree_AndExitDoesNotTouchOtherGameOrUserProcess()
    {
        var action = Script("first");
        Manager.Run("game", action);
        var first = Ready("first");
        Manager.Run("GAME", action);
        Assert.Equal(first, Ready("first"));
        Assert.Single(File.ReadAllLines(Path.Combine(_root, "first.started")));
        Manager.Run("other", Script("other"));
        var other = Ready("other");
        using var user = Process.Start(new ProcessStartInfo(action.Path)
        { Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep 120\"", UseShellExecute = false, CreateNoWindow = true })!;
        _pids.Add(user.Id);
        Manager.EndGame("game");
        Assert.True(SpinWait.SpinUntil(() => Dead(first), TimeSpan.FromSeconds(10)));
        Assert.False(Dead(other));
        Assert.False(user.HasExited);
        Manager.EndGame("other");
        Assert.True(SpinWait.SpinUntil(() => Dead(other), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ChildSurvivesRootExit_ButIsStillDeduplicatedAndCleaned()
    {
        var action = Script("child", child: true);
        Manager.Run("game", action);
        var child = Ready("child");
        var root = Ready("child.root");
        Assert.True(SpinWait.SpinUntil(() => Dead(root), TimeSpan.FromSeconds(10)));
        Manager.Run("game", action);
        Assert.Single(File.ReadAllLines(Path.Combine(_root, "child.started")));
        Manager.EndGame("game");
        Assert.True(SpinWait.SpinUntil(() => Dead(child), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void OptOutLeavesOwnedProgramRunning_AndDifferentArgumentsAreDistinct()
    {
        var action = Script("keep") with { StopOnGameExit = null };
        Manager.Run("game", action);
        var keep = Ready("keep");
        Manager.Run("game", Script("end"));
        var end = Ready("end");
        Manager.EndGame("game");
        Assert.False(Dead(keep));
        Assert.True(SpinWait.SpinUntil(() => Dead(end), TimeSpan.FromSeconds(10)));
    }

    private GameAction Script(string name, bool child = false)
    {
        var pidFile = Path.Combine(_root, name + ".pid");
        var script = Path.Combine(_root, name + ".ps1");
        var body = $"Add-Content -LiteralPath '{Path.Combine(_root, name + ".started")}' -Value started\n";
        if (child)
        {
            var childScript = Path.Combine(_root, "worker.ps1");
            File.WriteAllText(childScript, $"Set-Content -LiteralPath '{pidFile}' -Value $PID\nStart-Sleep 120");
            body += $"Set-Content -LiteralPath '{Path.Combine(_root, name + ".root.pid")}' -Value $PID\n";
            body += $"Start-Process -FilePath '{PowerShell}' -WindowStyle Hidden -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"{childScript}\"'\n";
        }
        else body += $"Set-Content -LiteralPath '{pidFile}' -Value $PID\nStart-Sleep 120";
        File.WriteAllText(script, body);
        return new() { When = "beforeLaunch", Type = "run", Path = PowerShell,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"", SingleInstance = true, StopOnGameExit = true };
    }
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private int Ready(string name)
    {
        int pid = 0;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { return int.TryParse(File.ReadAllText(Path.Combine(_root, name + ".pid")).Trim(), out pid) && pid > 0; }
            catch (IOException) { return false; }
        }, TimeSpan.FromSeconds(15)), "Companion did not become ready");
        _pids.Add(pid);
        return pid;
    }
    private static bool Dead(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    public void Dispose()
    {
        foreach (var game in new[] { "game", "other" }) { try { Manager.EndGame(game); } catch { } }
        foreach (var pid in _pids.Distinct())
            try { using var process = Process.GetProcessById(pid); if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch (ArgumentException) { }
        Directory.Delete(_root, true);
    }
}
