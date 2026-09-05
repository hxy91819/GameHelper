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
                Console.WriteLine($"✅ 已推送（提交 {outcome.CommitId ?? "unknown"}）");
                break;
            case SyncOutcomeStatus.Skipped:
                Console.WriteLine($"⏭️ 已跳过：{outcome.Detail}");
                break;
            case SyncOutcomeStatus.Validated:
                Console.WriteLine(outcome.Detail);
                break;
            case SyncOutcomeStatus.Failed:
                Console.WriteLine($"❌ 推送失败：{outcome.Error}");
                break;
        }

        Environment.ExitCode = outcome.Success ? 0 : 1;
    }

    private static async Task RunTestAsync(ISyncService syncService)
    {
        Console.WriteLine("正在校验推送渠道…");
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
                Console.WriteLine($"❌ 校验失败：{outcome.Error}");
                Environment.ExitCode = 1;
                break;
        }
    }

    private static void RunStatus(ISyncService syncService)
    {
        var status = syncService.GetStatus();
        if (!status.Configured)
        {
            Console.WriteLine("尚未配置统计推送（config.yml 中无 sync 段）。");
            return;
        }

        Console.WriteLine($"启用: {(status.Enabled ? "是" : "否")}");
        Console.WriteLine($"渠道: {status.Provider}（method={status.Method}）");
        Console.WriteLine($"仓库: {status.Repo}");
        Console.WriteLine($"目录: {status.Directory}");
        Console.WriteLine($"间隔: {status.IntervalMinutes} 分钟");
        if (status.ConfigError is not null)
        {
            Console.WriteLine($"配置错误: {status.ConfigError}");
        }

        Console.WriteLine($"上次成功: {FormatTime(status.LastSuccessUtc)}");
        Console.WriteLine($"上次尝试: {FormatTime(status.LastAttemptUtc)}");
        if (!string.IsNullOrEmpty(status.LastError))
        {
            Console.WriteLine($"上次错误: {status.LastError}");
        }

        Console.WriteLine($"本地有新数据待推送: {(status.HasPendingData ? "是" : "否")}");
    }

    private static string FormatTime(DateTime utc) =>
        utc == default ? "从未" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static void PrintUsage()
    {
        Console.WriteLine("用法:");
        Console.WriteLine("  sync now [--force]  立即执行一次推送（--force 跳过间隔与退避判断）");
        Console.WriteLine("  sync test           校验渠道配置与凭据（不写入数据）");
        Console.WriteLine("  sync status         查看推送状态");
    }
}
