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

    private readonly IFileDropRequestHandler _handler;
    private readonly ILogger<FileDropIpcServer> _logger;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _started;

    public FileDropIpcServer(IFileDropRequestHandler handler, ILogger<FileDropIpcServer> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
            _started = true;
            _logger.LogInformation("FileDrop IPC server started: Pipe={PipeName}", FileDropIpcProtocol.PipeName);
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loopTask;
        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _cts?.Cancel();
            loopTask = _loopTask;
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FileDrop IPC server stopped with non-fatal error");
            }
        }

        lock (_sync)
        {
            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    FileDropIpcProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
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
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
        await writer.WriteLineAsync(payload).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
