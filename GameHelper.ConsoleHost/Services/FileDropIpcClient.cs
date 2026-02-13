using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameHelper.ConsoleHost.Services;

internal static class FileDropIpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<DropAddResponse> SendAsync(
        string[] paths,
        string? configOverride,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        using var pipe = new NamedPipeClientStream(
            ".",
            FileDropIpcProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var request = new DropAddRequest
        {
            Paths = paths ?? Array.Empty<string>(),
            ConfigOverride = configOverride
        };

        var requestText = JsonSerializer.Serialize(request, JsonOptions);
        await writer.WriteLineAsync(requestText).ConfigureAwait(false);

        var responseText = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new DropAddResponse { Success = false, Error = "IPC response is empty." };
        }

        return JsonSerializer.Deserialize<DropAddResponse>(responseText, JsonOptions)
               ?? new DropAddResponse { Success = false, Error = "IPC response parse failed." };
    }
}
