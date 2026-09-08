using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using GameHelper.Infrastructure.Processes;
using GameHelper.Tests;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests.EndToEnd;

/// <summary>
/// 已发布 exe 的 monitor 命令产品级端到端验证：真实 ETW 内核会话 + 真实子进程 + 沙盒数据目录，
/// 覆盖宿主装配（Worker/GameAutomationService/CSV 持久化）到落盘的完整链路，以及 ETW 会话被外部
/// 杀死后（对应线上"游戏停止未检出"故障）的恢复与漏退出补偿。库级同构场景见
/// EtwSessionLossIntegrationTests；本文件补齐真实产物与宿主装配这层。
/// 需要管理员权限与已发布产物，任一不满足时跳过（与既有 ETW 集成测试同一约定）。
/// 启动方式：经 cmd /c 以文件重定向拉起（无控制台 + 管道 stdout 时应用启动会卡住，实测文件重定向正常），
/// 断言只匹配 ASCII 子串以兼容 GBK/UTF-8 输出。
/// </summary>
[Collection("ETW")]
public sealed class PublishedMonitorEndToEndTests(ITestOutputHelper output)
{
    private const string ProbeDataKey = "gh_e2e_probe";
    private const string ProbeExeName = "gh-e2e-probe.exe";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(75);

    [WindowsOnlyFact]
    public async Task Monitor_WhenElevatedWithPublishedExe_CapturesSessionIntoCsv()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        // 启动前快照：区分测试实例与用户实例/其他实例的 ETW 会话，清理时绝不触碰属主存活的会话。
        var preLaunchSessions = new EtwRuntime(null).GetActiveSessionNames();
        using var app = StartApp(exePath, sandbox);
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"),
                ReadyTimeout, "monitor host readiness (GameAutomationService started)");

            using var child = StartProbe(sandbox, "/c ping 127.0.0.1 -n 4 > NUL");
            await WaitUntilOutputAsync(app, sandbox,
                text => text.Contains("Process start") && text.Contains(ProbeDataKey),
                ReadyTimeout, "probe process start event");

            await WaitUntilAsync(() => ReadProbeRows(sandbox.CsvPath).Count > 0,
                PersistTimeout, "persisted playtime.csv row");

            var row = ReadProbeRows(sandbox.CsvPath)[0].Split(',');
            Assert.Equal(4, row.Length);
            Assert.False(string.IsNullOrWhiteSpace(row[1]), "start_time should be recorded");
            Assert.False(string.IsNullOrWhiteSpace(row[2]), "end_time should be recorded after probe exit");
            output.WriteLine($"Persisted row: {string.Join(',', row)}");
        }
        finally
        {
            StopAppAndSweepSessions(app);
        }
    }

    [WindowsOnlyFact]
    public async Task Monitor_WhenElevatedWithPublishedExe_ReconcilesGameExitDuringEtwSessionLoss()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var preLaunchSessions = new EtwRuntime(null).GetActiveSessionNames();
        using var app = StartApp(exePath, sandbox);
        Process? child = null;
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"),
                ReadyTimeout, "monitor host readiness (GameAutomationService started)");

            child = StartProbe(sandbox, "/c ping 127.0.0.1 -n 30 > NUL");
            await WaitUntilOutputAsync(app, sandbox,
                text => text.Contains("Process start") && text.Contains(ProbeDataKey),
                ReadyTimeout, "probe process start event");

            // 外部杀死应用的真实 ETW 内核会话（复现线上故障），随后立即退出游戏：
            // 恢复流程的重建延迟 >= 1 秒，退出事件必然落在断流窗口内，只能靠对账补偿。
            var runtime = new EtwRuntime(null);
            runtime.StopSession(ResolveNewAppSessionName(runtime, preLaunchSessions));
            child.Kill(entireProcessTree: true);

            await WaitUntilAsync(() => ReadProbeRows(sandbox.CsvPath).Count > 0,
                PersistTimeout, "reconciled playtime.csv row");
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("Reconciling missing process stop for PID"),
                TimeSpan.FromSeconds(10), "reconciliation warning log");
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("ETW session recovered on attempt"),
                TimeSpan.FromSeconds(10), "session recovery log");

            var row = ReadProbeRows(sandbox.CsvPath)[0].Split(',');
            Assert.Equal(4, row.Length);
            Assert.False(string.IsNullOrWhiteSpace(row[2]), "reconciled session must be completed with an end_time");
            output.WriteLine($"Reconciled row: {string.Join(',', row)}");
        }
        finally
        {
            if (child is not null && !child.HasExited) child.Kill(entireProcessTree: true);
            child?.Dispose();
            StopAppAndSweepSessions(app);
        }
    }

    private bool TryGuard(out string? exePath)
    {
        exePath = PublishedExeEndToEndTests.ResolvePublishExePath();
        if (exePath is null)
        {
            output.WriteLine("Skipped: published exe not found (run publish first per verify-and-deploy workflow).");
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            output.WriteLine("Skipped real ETW monitor verification: administrator required.");
            return false;
        }

        return true;
    }

    private static Sandbox CreateSandbox()
    {
        var sandbox = new Sandbox();
        File.WriteAllText(sandbox.ConfigPath, $"""
            monitor: ETW
            startup:
              autoStartMonitor: false
              launchOnStartup: false
            sync:
              enabled: false
            games:
              - dataKey: {ProbeDataKey}
                executable: {ProbeExeName}
                displayName: GH E2E Probe
                enabled: true
                hdr: false
            """);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), sandbox.ProbeExePath);
        return sandbox;
    }

    private static AppHandle StartApp(string exePath, Sandbox sandbox)
    {
        // 经 cmd /c 文件重定向拉起，复现已验证可用的无阻塞启动环境；stdin 预置一个换行。
        var arguments = $"/d /c \"\"{exePath}\" monitor < \"{sandbox.StdInFile}\"" +
                        $" > \"{sandbox.OutFile}\" 2> \"{sandbox.ErrFile}\"\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
        startInfo.EnvironmentVariables["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";

        return new AppHandle(Process.Start(startInfo)!);
    }

    private static Process StartProbe(Sandbox sandbox, string arguments) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = sandbox.ProbeExePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    private static string ResolveNewAppSessionName(EtwRuntime runtime, IReadOnlyCollection<string> preLaunchSessions)
    {
        var candidates = runtime.GetActiveSessionNames()
            .Except(preLaunchSessions, StringComparer.Ordinal)
            .Where(name => name.StartsWith("GameHelper-ETW-", StringComparison.Ordinal))
            .Where(name => !runtime.InspectProcess(ParseOwnerPid(name)).HasExited)
            .ToList();
        Assert.True(candidates.Count == 1,
            $"Expected exactly one new ETW session owned by the test app, found: [{string.Join(", ", candidates)}]");
        return candidates[0];
    }

    private static int ParseOwnerPid(string sessionName)
    {
        var remainder = sessionName["GameHelper-ETW-".Length..];
        return int.Parse(remainder[..remainder.IndexOf('-')]);
    }

    /// <summary>
    /// 强杀测试实例（cmd 进程树，专属沙盒实例，绝非用户实例），随后只清理属主已死亡的
    /// GameHelper ETW 会话；属主存活的会话可能是用户实例，绝不动。
    /// </summary>
    private static void StopAppAndSweepSessions(AppHandle app)
    {
        app.Kill();
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var runtime = new EtwRuntime(null);
        foreach (var name in runtime.GetActiveSessionNames())
        {
            if (!name.StartsWith("GameHelper-ETW-", StringComparison.Ordinal)) continue;
            var ownerPid = ParseOwnerPid(name);
            // 属主已死才清理；PID 复用导致的误判只是留下孤儿会话，方向是安全的。
            if (!runtime.InspectProcess(ownerPid).HasExited) continue;
            try { runtime.StopSession(name); } catch { /* 测试清理尽力而为 */ }
        }
    }

    /// <summary>以共享读写方式读取重定向文件；GBK/UTF-8 差异不影响 ASCII 断言。</summary>
    private static string ReadTextFile(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static List<string> ReadProbeRows(string csvPath) =>
        ReadTextFile(csvPath)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(ProbeDataKey + ",", StringComparison.Ordinal))
            .ToList();

    private async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < timeout) await Task.Delay(100);
        Assert.True(condition(), $"Expected {description} within {timeout.TotalSeconds:F0} seconds.");
    }

    private async Task WaitUntilOutputAsync(AppHandle app, Sandbox sandbox, Func<string, bool> predicate,
        TimeSpan timeout, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate(AppOutput(sandbox)) && timer.Elapsed < timeout) await Task.Delay(100);
        if (!predicate(AppOutput(sandbox)))
        {
            output.WriteLine($"[dump] app output while waiting for '{description}':");
            foreach (var line in AppOutput(sandbox).Split('\n').TakeLast(40)) output.WriteLine("[dump] " + line.TrimEnd('\r'));
        }

        Assert.True(predicate(AppOutput(sandbox)), $"Expected {description} within {timeout.TotalSeconds:F0} seconds.");
    }

    private static string AppOutput(Sandbox sandbox) => ReadTextFile(sandbox.OutFile) + ReadTextFile(sandbox.ErrFile);

    private sealed class AppHandle(Process process) : IDisposable
    {
        public void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* 进程可能已退出 */ }
            try { process.WaitForExit(5_000); } catch { /* 尽力等待 */ }
        }

        public void Dispose() => Kill();
    }

    /// <summary>独立的数据沙盒：目录隔离，测试后自动清理，绝不触碰真实 AppData。</summary>
    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "gh-e2e-monitor", Guid.NewGuid().ToString("N"));
            GameHelperDir = Path.Combine(RootDir, "GameHelper");
            Directory.CreateDirectory(GameHelperDir);
            ConfigPath = Path.Combine(GameHelperDir, "config.yml");
            CsvPath = Path.Combine(GameHelperDir, "playtime.csv");
            ProbeExePath = Path.Combine(RootDir, ProbeExeName);
            StdInFile = Path.Combine(RootDir, "stdin.txt");
            OutFile = Path.Combine(RootDir, "app-out.log");
            ErrFile = Path.Combine(RootDir, "app-err.log");
            File.WriteAllText(StdInFile, "\n");
        }

        public string RootDir { get; }
        public string GameHelperDir { get; }
        public string ConfigPath { get; }
        public string CsvPath { get; }
        public string ProbeExePath { get; }
        public string StdInFile { get; }
        public string OutFile { get; }
        public string ErrFile { get; }

        public void Dispose()
        {
            try { Directory.Delete(RootDir, recursive: true); }
            catch { /* 沙盒清理失败不应掩盖测试结果。 */ }
        }
    }
}
