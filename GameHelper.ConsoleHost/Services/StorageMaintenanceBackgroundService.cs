using GameHelper.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Hosting;

namespace GameHelper.ConsoleHost.Services;

/// <summary>Runs maintenance independently of automation state locks and event callbacks.</summary>
internal sealed class StorageMaintenanceBackgroundService(SessionStorage storage, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do { await storage.MaintainAsync(stoppingToken).ConfigureAwait(false); }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
