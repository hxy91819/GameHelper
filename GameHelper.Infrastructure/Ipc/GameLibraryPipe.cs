using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Ipc;

/// <summary>User- and config-scoped local transport with bounded messages and operation deadlines.</summary>
public sealed class GameLibraryPipe(string configPath) : IGameLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    public static string NameFor(string path)
    {
        var identity = WindowsIdentity.GetCurrent();
        using (identity)
        {
            var key = identity.User?.Value + "|" + Path.GetFullPath(path).ToUpperInvariant();
            return "GameHelper.Library.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        }
    }

    public async Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await using var pipe = new NamedPipeClientStream(".", NameFor(configPath), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // Fast failure lets the desktop offer to start the normal host instead of hanging for a minute.
        await pipe.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
        await WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<GameLibraryResponse>(pipe, timeout.Token).ConfigureAwait(false);
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("游戏库消息过大。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("游戏库消息长度无效。");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("游戏库消息为空。");
    }
}
