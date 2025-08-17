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
        private readonly IProcessMonitor _monitor;
        private readonly IHdrController _hdr;
        private readonly IGameAutomationService _automation;

        public Worker(ILogger<Worker> logger, IProcessMonitor monitor, IHdrController hdr, IGameAutomationService automation)
        {
            _logger = logger;
            _monitor = monitor;
            _hdr = hdr;
            _automation = automation;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GameHelper ConsoleHost started");
            _automation.Start();
            _monitor.Start();

            stoppingToken.Register(() =>
            {
                _monitor.Stop();
                _automation.Stop();
                _logger.LogInformation("GameHelper ConsoleHost stopping");
            });

            return Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}
