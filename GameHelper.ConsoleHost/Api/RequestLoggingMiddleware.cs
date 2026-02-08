using System.Diagnostics;
using GameHelper.Core.Utilities;

namespace GameHelper.ConsoleHost.Api;

/// <summary>
/// Lightweight middleware that logs HTTP requests to a file at %AppData%/GameHelper/web.log.
/// Each line: [timestamp] METHOD /path STATUS elapsed_ms
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public RequestLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
        var dir = AppDataPath.GetGameHelperDirectory();
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, "web.log");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
            sw.Stop();
            WriteLog(context, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            WriteLog(context, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    private void WriteLog(HttpContext context, long elapsedMs, Exception? error)
    {
        var method = context.Request.Method;
        var path = context.Request.Path + context.Request.QueryString;
        var status = context.Response.StatusCode;
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        var line = error == null
            ? $"[{timestamp}] {method} {path} {status} {elapsedMs}ms"
            : $"[{timestamp}] {method} {path} {status} {elapsedMs}ms ERROR: {error.Message}";

        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Silently ignore write failures to avoid crashing the pipeline
        }
    }
}
