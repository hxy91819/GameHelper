using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Commands;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameHelper.ConsoleHost;

public static class ConsoleCommandDispatcher
{
    public static async Task DispatchAsync(IHost host, ParsedArguments parsedArgs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(parsedArgs);

        if (parsedArgs.EffectiveArgs.Length == 0)
        {
            CommandHelpers.PrintUsage();
            return;
        }

        var command = parsedArgs.EffectiveArgs[0].ToLowerInvariant();
        switch (command)
        {
            case "monitor":
                await host.RunAsync(cancellationToken).ConfigureAwait(false);
                break;

            case "config":
                using (var scope = host.Services.CreateScope())
                {
                    ConfigCommand.Run(
                        scope.ServiceProvider.GetRequiredService<IGameCatalogService>(),
                        scope.ServiceProvider.GetService<ISteamGameResolver>(),
                        parsedArgs.EffectiveArgs.Skip(1).ToArray());
                }
                break;

            case "stats":
                using (var scope = host.Services.CreateScope())
                {
                    StatsCommand.Run(
                        scope.ServiceProvider.GetRequiredService<IStatisticsService>(),
                        parsedArgs.EffectiveArgs.Skip(1).ToArray());
                }
                break;

            case "sync":
                await SyncCommand.RunAsync(
                    host.Services.GetRequiredService<ISyncService>(),
                    parsedArgs.EffectiveArgs.Skip(1).ToArray()).ConfigureAwait(false);
                break;

            case "convert-config":
                ConvertConfigCommand.Run();
                break;

            case "validate-config":
                ValidateConfigCommand.Run(parsedArgs.ConfigOverride);
                break;

            case "migrate":
            case "migrate-config":
                MigrateCommand.Run(parsedArgs.EffectiveArgs.Skip(1).ToArray());
                break;

            default:
                CommandHelpers.PrintUsage();
                break;
        }
    }
}
