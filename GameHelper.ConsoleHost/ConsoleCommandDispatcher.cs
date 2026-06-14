using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Commands;
using GameHelper.ConsoleHost.Interactive;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameHelper.ConsoleHost;

public static class ConsoleCommandDispatcher
{
    public static async Task DispatchAsync(IHost host, ParsedArguments parsedArgs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(parsedArgs);

        var interactiveMode = parsedArgs.UseInteractiveShell || parsedArgs.EffectiveArgs.Length == 0;
        if (interactiveMode)
        {
            await RunInteractiveAsync(host, parsedArgs, cancellationToken).ConfigureAwait(false);
            return;
        }

        var command = parsedArgs.EffectiveArgs[0].ToLowerInvariant();
        switch (command)
        {
            case "monitor":
                await host.RunAsync(cancellationToken).ConfigureAwait(false);
                break;

            case "config":
                ConfigCommand.Run(host.Services, parsedArgs.EffectiveArgs.Skip(1).ToArray());
                break;

            case "stats":
                StatsCommand.Run(host.Services, parsedArgs.EffectiveArgs.Skip(1).ToArray());
                break;

            case "convert-config":
                ConvertConfigCommand.Run();
                break;

            case "validate-config":
                ValidateConfigCommand.Run();
                break;

            case "migrate":
            case "migrate-config":
                MigrateCommand.Run(parsedArgs.EffectiveArgs.Skip(1).ToArray());
                break;

            case "interactive":
                await RunInteractiveAsync(host, parsedArgs, cancellationToken).ConfigureAwait(false);
                break;

            default:
                CommandHelpers.PrintUsage();
                break;
        }
    }

    private static async Task RunInteractiveAsync(IHost host, ParsedArguments parsedArgs, CancellationToken cancellationToken)
    {
        var ipcServer = host.Services.GetRequiredService<IFileDropIpcServer>();
        await ipcServer.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var shell = new InteractiveShell(host, parsedArgs);
            await shell.RunAsync();
        }
        finally
        {
            await ipcServer.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
