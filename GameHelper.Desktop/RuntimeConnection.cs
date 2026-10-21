using System.Diagnostics;
using System.IO;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Ipc;
using GameHelper.Infrastructure.Startup;

namespace GameHelper.Desktop;

internal static class RuntimeConnection
{
    public static async Task<GameLibraryResponse> EnsureAsync(string config)
    {
        var pipe = new GameLibraryPipe(config);
        try { return await ReadAsync(pipe); }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }

        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "GameHelper.ConsoleHost.exe"));
        if (!File.Exists(host)) throw new FileNotFoundException("请先启动监控运行时；发布版本会自动在后台启动。", host);
        using var process = Process.Start(BackgroundRuntime.CreateStartInfo(host, config))
            ?? throw new InvalidOperationException("无法启动后台监控。");
        try
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(25))
            {
                try { return await ReadAsync(pipe); }
                catch (Exception ex) when (ex is IOException or TimeoutException) { }
                if (process.HasExited && process.ExitCode != 0) break;
                await Task.Delay(250);
            }
            throw new InvalidOperationException("后台监控未就绪，请检查配置与 config.yml.runtime.log。");
        }
        catch
        {
            // Never leave an unusable hidden instance holding the single-instance mutex.
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            throw;
        }
    }

    private static async Task<GameLibraryResponse> ReadAsync(GameLibraryPipe pipe)
    {
        var response = await pipe.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List });
        if (!response.Success) throw new InvalidOperationException(response.Error);
        return response;
    }
}
