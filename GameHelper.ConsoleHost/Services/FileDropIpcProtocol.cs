using System;
using System.Text;

namespace GameHelper.ConsoleHost.Services;

internal static class FileDropIpcProtocol
{
    public const string PipeName = "GameHelper.ConsoleHost.FileDrop";
    internal const int MaxMessageBytes = 1024 * 1024;

    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken,
        TimeProvider? clock = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var text = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
            if (count == 0) return text.Length == 0 ? null : Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length).TrimEnd('\r');
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var length = newline < 0 ? count : newline;
            if (text.Length + length > MaxMessageBytes)
                throw new InvalidDataException("File-drop IPC message exceeds the 1 MiB limit.");
            text.Write(buffer, 0, length);
            if (newline >= 0) return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length).TrimEnd('\r');
        }
    }
}

internal sealed class DropAddRequest
{
    public string[] Paths { get; set; } = Array.Empty<string>();

}

internal sealed class DropAddResponse
{
    public bool Success { get; set; }

    public int Added { get; set; }

    public int Updated { get; set; }

    public int Skipped { get; set; }

    public string ConfigPath { get; set; } = string.Empty;

    public string? Error { get; set; }
}
