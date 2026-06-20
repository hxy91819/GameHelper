using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IMonitorControlService _monitorControlService;

        public Worker(
            ILogger<Worker> logger,
            IMonitorControlService monitorControlService)
        {
            _logger = logger;
            _monitorControlService = monitorControlService;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GameHelper ConsoleHost started");

            _monitorControlService.Start();

            stoppingToken.Register(() =>
            {
                _monitorControlService.Stop();
                _logger.LogInformation("GameHelper ConsoleHost stopping");
            });

            return Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}
