using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Commands;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
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

            case "storage":
                var configProvider = host.Services.GetRequiredService<IGameConfiguration>();
                var configPath = ((IConfigPathProvider)configProvider).ConfigPath;
                LegacyWriterGuard.EnsureDesktopStopped(configPath);
                var maintenance = new SqliteLegacyCompatibility(host.Services.GetRequiredService<SqliteConnectionFactory>(),
                    host.Services.GetRequiredService<TimeProvider>(), configProvider.Read().Games, preserveHistoryKinds: true);
                if (parsedArgs.EffectiveArgs.ElementAtOrDefault(1) == "prepare-downgrade")
                    Console.WriteLine(await maintenance.PrepareDowngradeAsync(cancellationToken).ConfigureAwait(false));
                else if (parsedArgs.EffectiveArgs.ElementAtOrDefault(1) == "reupgrade" && parsedArgs.EffectiveArgs.Length == 3)
                    await maintenance.ReupgradeAsync(parsedArgs.EffectiveArgs[2], cancellationToken).ConfigureAwait(false);
                else throw new ArgumentException("storage prepare-downgrade | storage reupgrade <manifest>");
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

            case "validate-config":
                ValidateConfigCommand.Run(parsedArgs.ConfigOverride);
                break;

            default:
                CommandHelpers.PrintUsage();
                break;
        }
    }
}
