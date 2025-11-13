using System.Linq;
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
        private readonly IConfigProvider _configProvider;

        public Worker(
            ILogger<Worker> logger,
            IProcessMonitor monitor,
            IHdrController hdr,
            IGameAutomationService automation,
            IConfigProvider configProvider)
        {
            _logger = logger;
            _monitor = monitor;
            _hdr = hdr;
            _automation = automation;
            _configProvider = configProvider;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GameHelper ConsoleHost started");

            // Detect old format configuration and warn user
            DetectOldFormatConfiguration();

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

        private void DetectOldFormatConfiguration()
        {
            try
            {
                var configs = _configProvider.Load();
                
                // Check if any game is missing DataKey (old format)
                var oldFormatGames = configs.Values
                    .Where(g => string.IsNullOrEmpty(g.DataKey))
                    .ToList();

                if (oldFormatGames.Any())
                {
                    _logger.LogWarning(
                        "检测到旧格式配置（{Count} 个游戏缺少 dataKey 字段）。" +
                        "建议运行 'migrate' 命令进行迁移以获得更好的体验。",
                        oldFormatGames.Count);

                    // Show console warning with yellow color
                    System.Console.ForegroundColor = System.ConsoleColor.Yellow;
                    System.Console.WriteLine("⚠️  检测到旧格式配置，建议运行 'migrate' 命令进行迁移。");
                    System.Console.ResetColor();
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogDebug(ex, "配置格式检测失败（非致命错误）");
            }
        }
    }
}
