using GameHelper.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Api;

public sealed class WebServerHost : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly IServiceProvider _hostServices;
    private readonly int _port;
    private readonly ILogger _logger;

    public WebServerHost(IServiceProvider hostServices, int port, ILogger logger)
    {
        _hostServices = hostServices;
        _port = port;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(_port);
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register services from the existing host DI container
        builder.Services.AddSingleton(_hostServices.GetRequiredService<IConfigProvider>());
        builder.Services.AddSingleton(_hostServices.GetRequiredService<IAppConfigProvider>());

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        _app = builder.Build();

        _app.UseMiddleware<RequestLoggingMiddleware>();
        _app.UseCors();

        // Serve static files from wwwroot if it exists (for production builds)
        var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwrootPath))
        {
            _app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath)
            });
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath)
            });
        }

        // Map API endpoints
        _app.MapGameEndpoints();
        _app.MapSettingsEndpoints();
        _app.MapStatsEndpoints();

        _logger.LogInformation("Starting web server on http://127.0.0.1:{Port}", _port);
        await _app.StartAsync(cancellationToken);
        _logger.LogInformation("Web server started: http://127.0.0.1:{Port}", _port);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app != null)
        {
            await _app.StopAsync(cancellationToken);
            _logger.LogInformation("Web server stopped.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
