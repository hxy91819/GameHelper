using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Models;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Services;

internal interface IFileDropRequestHandler
{
    Task<DropAddResponse> HandleAsync(DropAddRequest request, CancellationToken cancellationToken);
}

internal interface IFileDropProcessor
{
    bool LooksLikeFilePaths(string[] paths);

    AddSummary ProcessFilePaths(string[] paths, string? configOverride, IServiceProvider services);
}

internal sealed class DefaultFileDropProcessor : IFileDropProcessor
{
    public bool LooksLikeFilePaths(string[] paths) => FileDropHandler.LooksLikeFilePaths(paths);

    public AddSummary ProcessFilePaths(string[] paths, string? configOverride, IServiceProvider services) =>
        FileDropHandler.ProcessFilePaths(paths, configOverride, services);
}

internal sealed class FileDropRequestHandler : IFileDropRequestHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IServiceProvider _services;
    private readonly IFileDropProcessor _processor;
    private readonly IGameAutomationService _automationService;
    private readonly ILogger<FileDropRequestHandler> _logger;

    public FileDropRequestHandler(
        IServiceProvider services,
        IFileDropProcessor processor,
        IGameAutomationService automationService,
        ILogger<FileDropRequestHandler> logger)
    {
        _services = services;
        _processor = processor;
        _automationService = automationService;
        _logger = logger;
    }

    public async Task<DropAddResponse> HandleAsync(DropAddRequest request, CancellationToken cancellationToken)
    {
        if (request.Paths is null || request.Paths.Length == 0)
        {
            return new DropAddResponse { Success = false, Error = "No file paths provided." };
        }

        if (!_processor.LooksLikeFilePaths(request.Paths))
        {
            return new DropAddResponse { Success = false, Error = "Invalid drag-drop payload. Only existing .exe/.lnk/.url files are accepted." };
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var summary = _processor.ProcessFilePaths(request.Paths, request.ConfigOverride, _services);
            _automationService.ReloadConfig();

            var response = new DropAddResponse
            {
                Success = true,
                Added = summary.Added,
                Updated = summary.Updated,
                Skipped = summary.Skipped,
                DuplicatesRemoved = summary.DuplicatesRemoved,
                ConfigPath = summary.ConfigPath
            };

            _logger.LogInformation(
                "IPC file-drop handled in {ElapsedMs}ms: Added={Added}, Updated={Updated}, Skipped={Skipped}",
                sw.ElapsedMilliseconds,
                response.Added,
                response.Updated,
                response.Skipped);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC file-drop handling failed");
            return new DropAddResponse { Success = false, Error = ex.Message };
        }
        finally
        {
            sw.Stop();
            _gate.Release();
        }
    }
}
