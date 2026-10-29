using System.IO.Pipes;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Ipc;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Services;

internal sealed class GameLibraryServer(IGameLibrary library, IConfigPathProvider config, ILogger<GameLibraryServer> logger, ConsoleRuntimeActions? console = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(GameLibraryPipe.NameFor(config.ConfigPath), PipeDirection.InOut,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var request = await GameLibraryPipe.ReadAsync<GameLibraryRequest>(pipe, timeout.Token).ConfigureAwait(false);
                GameLibraryResponse response;
                try
                {
                    response = (await (ConsoleRuntimeActions.Handles(request.Operation) && console is not null
                        ? console.ExecuteAsync(request, timeout.Token) : library.ExecuteAsync(request, timeout.Token)).ConfigureAwait(false))
                        with { RuntimeProcessId = Environment.ProcessId };
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Game library request failed: {Operation}", request.Operation);
                    response = new GameLibraryResponse { Error = ex.Message };
                }
                await GameLibraryPipe.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Library connection ended");
                try { await Task.Delay(100, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
