using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Services;

internal interface IFileDropIpcServer
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class FileDropIpcServer : IHostedService, IFileDropIpcServer, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FileDropIntake _handler;
    private readonly ILogger<FileDropIpcServer> _logger;
    private readonly string _pipeName;
    private readonly TimeProvider _clock;
    private readonly BackgroundLoopLifecycle _lifecycle;

    public FileDropIpcServer(FileDropIntake handler, ILogger<FileDropIpcServer> logger,
        string? pipeName = null, TimeProvider? clock = null)
    {
        _handler = handler;
        _logger = logger;
        _pipeName = pipeName ?? FileDropIpcProtocol.PipeName;
        _clock = clock ?? TimeProvider.System;
        _lifecycle = new BackgroundLoopLifecycle(ListenLoopAsync, logger);
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifecycle.Start(cancellationToken))
            _logger.LogInformation("FileDrop IPC server started: Pipe={PipeName}", _pipeName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _lifecycle.StopAsync(cancellationToken);

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("FileDrop IPC request deadline exceeded; accepting the next client");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FileDrop IPC server loop error");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        var line = await FileDropIpcProtocol.ReadLineAsync(stream, cancellationToken, _clock).ConfigureAwait(false);
        DropAddResponse response;
        if (string.IsNullOrWhiteSpace(line))
        {
            response = new DropAddResponse { Success = false, Error = "Empty IPC request." };
        }
        else
        {
            try
            {
                var request = JsonSerializer.Deserialize<DropAddRequest>(line, JsonOptions) ?? new DropAddRequest();
                response = await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse/process IPC request");
                response = new DropAddResponse { Success = false, Error = ex.Message };
            }
        }

        var payload = JsonSerializer.Serialize(response, JsonOptions);
        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _lifecycle.Dispose();
}
