using System;
using System.Linq;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;

namespace GameHelper.ConsoleHost.Commands;

/// <summary>
/// `sync` 命令：now（立即推送）/ test（校验渠道）/ status（查看状态）。
/// </summary>
public static class SyncCommand
{
    public static async Task RunAsync(ISyncService syncService, string[] args)
    {
        var subCommand = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (subCommand)
        {
            case "now":
                await RunNowAsync(syncService, args.Skip(1).ToArray()).ConfigureAwait(false);
                break;

            case "test":
                await RunTestAsync(syncService).ConfigureAwait(false);
                break;

            case "status":
                RunStatus(syncService);
                break;

            default:
                PrintUsage();
                Environment.ExitCode = 1;
                break;
        }
    }

    private static async Task RunNowAsync(ISyncService syncService, string[] args)
    {
        var force = args.Any(arg => arg is "--force" or "-f");
        var outcome = await syncService.SyncNowAsync(force).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case SyncOutcomeStatus.Uploaded:
                Console.WriteLine($"✅ Pushed (commit {outcome.CommitId ?? "unknown"})");
                break;
            case SyncOutcomeStatus.Skipped:
                Console.WriteLine($"⏭️ Skipped: {outcome.Detail}");
                break;
            case SyncOutcomeStatus.Validated:
                Console.WriteLine(outcome.Detail);
                break;
            case SyncOutcomeStatus.Failed:
                Console.WriteLine($"❌ Push failed: {outcome.Error}");
                break;
        }

        Environment.ExitCode = outcome.Success ? 0 : 1;
    }

    private static async Task RunTestAsync(ISyncService syncService)
    {
        Console.WriteLine("Validating sync channel...");
        var outcome = await syncService.ValidateAsync().ConfigureAwait(false);
        switch (outcome.Status)
        {
            case SyncOutcomeStatus.Validated:
                Console.WriteLine($"✅ {outcome.Detail}");
                break;
            case SyncOutcomeStatus.Skipped:
                Console.WriteLine($"⏭️ {outcome.Detail}");
                Environment.ExitCode = 1;
                break;
            case SyncOutcomeStatus.Failed:
                Console.WriteLine($"❌ Validation failed: {outcome.Error}");
                Environment.ExitCode = 1;
                break;
        }
    }

    private static void RunStatus(ISyncService syncService)
    {
        var status = syncService.GetStatus();
        if (!status.Configured)
        {
            Console.WriteLine("Stats sync is not configured (no 'sync' section in config.yml).");
            return;
        }

        Console.WriteLine($"Enabled: {(status.Enabled ? "yes" : "no")}");
        Console.WriteLine($"Channel: {status.Provider} (method={status.Method})");
        Console.WriteLine($"Repo: {status.Repo}");
        Console.WriteLine($"Directory: {status.Directory}");
        Console.WriteLine($"Interval: {status.IntervalMinutes} min");
        if (status.ConfigError is not null)
        {
            Console.WriteLine($"Config error: {status.ConfigError}");
        }

        Console.WriteLine($"Last success: {FormatTime(status.LastSuccessUtc)}");
        Console.WriteLine($"Last attempt: {FormatTime(status.LastAttemptUtc)}");
        if (!string.IsNullOrEmpty(status.LastError))
        {
            Console.WriteLine($"Last error: {status.LastError}");
        }

        Console.WriteLine($"Pending local data to push: {(status.HasPendingData ? "yes" : "no")}");
    }

    private static string FormatTime(DateTime utc) =>
        utc == default ? "never" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  sync now [--force]  Run a sync immediately (--force skips interval and backoff checks)");
        Console.WriteLine("  sync test           Validate channel config and credentials (writes no data)");
        Console.WriteLine("  sync status         Show sync status");
    }
}
