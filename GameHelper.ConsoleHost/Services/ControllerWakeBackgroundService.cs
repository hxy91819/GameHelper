using System.Diagnostics;
using GameHelper.Core.Abstractions;
using GameHelper.Infrastructure.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Services;

/// <summary>Keeps the windowless controller broker alive for the tray runtime.</summary>
internal sealed class ControllerWakeBackgroundService(IConfigPathProvider config,
    ILogger<ControllerWakeBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Process? broker = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (broker is null || broker.HasExited)
                    {
                        broker?.Dispose();
                        broker = null;
                        var start = SystemTrayLogic.LibraryStartInfo(AppContext.BaseDirectory, config.ConfigPath, File.Exists);
                        start.UseShellExecute = false;
                        start.CreateNoWindow = true;
                        start.ArgumentList.Add("--controller-wake");
                        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        broker = Process.Start(start);
                    }
                }
                catch (Exception ex) { logger.LogWarning(ex, "Unable to start controller wake listener"); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { if (broker is not null && !broker.HasExited) broker.Kill(); }
            catch (Exception ex) { logger.LogWarning(ex, "Unable to stop controller wake listener"); }
            finally { broker?.Dispose(); }
        }
    }
}
