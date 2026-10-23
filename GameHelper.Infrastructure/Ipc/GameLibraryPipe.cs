using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
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
        // 不用 PipeOptions.CurrentUserOnly：其 Windows 实现按"令牌默认所有者"建 ACL 并校验，
        // 而同一账户下服务型宿主与交互会话铸造的令牌所有者会在 用户SID/Administrators 之间漂移，
        // 提权部署链拉起的运行时会被用户双击的游戏库误判为"不属于当前用户"。
        // 改为连接后按服务端进程的 TokenUser（恒等于账户 SID，不随铸造方式漂移）校验。
        await using var pipe = new NamedPipeClientStream(".", NameFor(configPath), PipeDirection.InOut, PipeOptions.Asynchronous);
        // Fast failure lets the desktop offer to start the normal host instead of hanging for a minute.
        await pipe.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
        EnsurePipeOwnedByCurrentUser(pipe);
        await WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<GameLibraryResponse>(pipe, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 校验管道服务端进程与当前客户端属于同一 Windows 账户（TokenUser 口径，覆盖任意令牌铸造方式）。
    /// 不同账户一律拒绝，保持与 PipeOptions.CurrentUserOnly 等价的安全语义（错误消息一致）。
    /// </summary>
    internal static void EnsurePipeOwnedByCurrentUser(NamedPipeClientStream pipe)
    {
        if (OperatingSystem.IsWindows() == false) return;
        var serverPid = NativePipe.GetServerProcessId(pipe.SafePipeHandle);
        if (serverPid <= 0)
        {
            // 无服务端进程身份可取证时按旧行为保守拒绝。
            throw new UnauthorizedAccessException("Could not connect to the pipe because it was not owned by the current user.");
        }

        var serverUser = NativePipe.ProcessTokenUser(serverPid);
        var currentUser = WindowsIdentity.GetCurrent().User?.Value;
        if (serverUser is null || currentUser is null
            || !string.Equals(serverUser, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Could not connect to the pipe because it was not owned by the current user.");
        }
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

    private static class NativePipe
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int TOKEN_QUERY = 0x0008;
        private const int TOKEN_DUPLICATE = 0x0002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>返回连接中的管道客户端句柄对应的服务端进程 ID；取不到时为 0。</summary>
        internal static uint GetServerProcessId(SafePipeHandle pipe)
        {
            return GetNamedPipeServerProcessId(pipe, out var serverPid) ? serverPid : 0;
        }

        /// <summary>按进程主令牌读其所属账户的 SID（字符串形式）；失败时为 null。
        /// 经 WindowsIdentity 构造函数读取：本机环境拦截裸 GetTokenInformation（错误 87），
        /// .NET 内部查询路径可用，且跨令牌铸造方式稳定。</summary>
        internal static string? ProcessTokenUser(uint pid)
        {
            var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, inherit: false, pid);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (!OpenProcessToken(process, TOKEN_QUERY | TOKEN_DUPLICATE, out var token))
                {
                    return null;
                }

                try
                {
                    using var identity = new WindowsIdentity(token);
                    return identity.User?.Value;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }
}
