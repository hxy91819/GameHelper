using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace GameHelper.Tests;

[Collection("ETW")]
public sealed class EtwSessionLossIntegrationTests(ITestOutputHelper output)
{
    [WindowsOnlyFact]
    public async Task SessionLoss_GameExitsDuringGap_RecordsBothThatGameAndNextGame()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            output.WriteLine("Skipped real ETW session-loss verification: administrator required.");
            return;
        }

        var sandbox = Path.Combine(Path.GetTempPath(), "GameHelper.EtwRecovery." + Guid.NewGuid().ToString("N"));
        var runtime = new ControlledRuntime();
        using var witness = new EtwProcessMonitor(new[] { "unused-etw-witness.exe" });
        witness.Start();
        var otherSessions = runtime.GetActiveSessionNames().Where(n => n.StartsWith("GameHelper-ETW-", StringComparison.Ordinal)).ToArray();
        using var monitor = new EtwProcessMonitor(runtime);
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var config = new Mock<IGameConfiguration>();
        config.Setup(c => c.Read()).Returns(new AppConfig
        {
            Games = new List<GameConfig> { new() { DataKey = "etw_recovery", Executable = cmdPath, IsEnabled = true } }
        });
        var automation = new GameAutomationService(monitor, config.Object, Mock.Of<IHdrController>(),
            new CsvBackedPlayTimeService(sandbox), NullLogger<GameAutomationService>.Instance, new WindowsProcessPathResolver());
        var starts = new ConcurrentDictionary<int, bool>();
        monitor.ProcessStarted += info => { if (info.ProcessId is int pid) starts.TryAdd(pid, true); };
        var children = new List<Process>();
        try
        {
            automation.Start();
            monitor.Start();
            var first = StartChild(cmdPath);
            children.Add(first);
            await UntilAsync(() => starts.ContainsKey(first.Id));
            runtime.StopOwnedSession();
            await runtime.RecoveryWaiting.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await ExitChildAsync(first);
            runtime.Resume.TrySetResult();
            await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(15));
            await UntilAsync(() => CountSessions(sandbox) == 1);

            var second = StartChild(cmdPath);
            children.Add(second);
            await UntilAsync(() => starts.ContainsKey(second.Id));
            await ExitChildAsync(second);
            await UntilAsync(() => CountSessions(sandbox) == 2);
            Assert.All(otherSessions, name => Assert.Contains(name, runtime.GetActiveSessionNames()));
            output.WriteLine("Real ETW session loss: gap exit persisted; next start/stop persisted; other sessions preserved.");
        }
        finally
        {
            monitor.Stop();
            automation.Stop();
            foreach (var child in children)
            {
                if (!child.HasExited) child.Kill();
                child.Dispose();
            }
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        }
    }

    private static Process StartChild(string executable) => Process.Start(new ProcessStartInfo
    {
        FileName = executable, Arguments = "/d /c set /p gh_test=",
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    })!;

    private static async Task ExitChildAsync(Process child)
    {
        await child.StandardInput.WriteLineAsync("done");
        await child.StandardInput.FlushAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int CountSessions(string sandbox)
    {
        var path = Path.Combine(sandbox, "playtime.csv");
        return File.Exists(path) ? File.ReadAllLines(path).Count(line => line.StartsWith("etw_recovery,", StringComparison.Ordinal)) : 0;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
        Assert.True(condition(), "Expected process notification / persisted CSV row within 15 seconds");
    }

    private sealed class ControlledRuntime : IEtwRuntime
    {
        private readonly EtwRuntime _inner = new(null);
        private string? _ownedSession;
        public TaskCompletionSource RecoveryWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void EnsureElevated() => _inner.EnsureElevated();
        public IEtwSession CreateSession(string name)
        {
            _ownedSession = name;
            return _inner.CreateSession(name);
        }
        public IReadOnlyCollection<string> GetActiveSessionNames() => _inner.GetActiveSessionNames();
        public ProcessProbe InspectProcess(int processId) => _inner.InspectProcess(processId);
        public void StopSession(string name) => throw new InvalidOperationException("Integration tests may stop only their explicitly owned session");
        public void StopOwnedSession() => _inner.StopSession(_ownedSession ?? throw new InvalidOperationException("No test session created"));
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            RecoveryWaiting.TrySetResult();
            return Resume.Task.WaitAsync(cancellationToken);
        }
    }
}
