using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IMonitorControlService _monitorControlService;

    private readonly ISystemTrayService? _tray;

    public Worker(
        ILogger<Worker> logger,
        IMonitorControlService monitorControlService,
        ISystemTrayService? tray = null)
    {
        _logger = logger;
        _monitorControlService = monitorControlService;
        _tray = tray;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GameHelper ConsoleHost started");
        try
        {
            _monitorControlService.Start();
            _tray?.Start();
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        finally
        {
            try { _tray?.Stop(); }
            finally
            {
                _monitorControlService.Stop();
                _logger.LogInformation("GameHelper ConsoleHost stopping");
            }
        }
    }
}
