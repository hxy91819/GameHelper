using System.Diagnostics;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.ConsoleHost.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Services;

/// <summary>
/// Owns the complete File-drop Intake workflow from validation through Catalog commit and automation reload.
/// </summary>
internal sealed class FileDropIntake
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IGameCatalogService _gameCatalog;
    private readonly ISteamGameResolver _steamResolver;
    private readonly IGameAutomationService _automation;
    private readonly IConfigPathProvider _configPath;
    private readonly ILogger<FileDropIntake> _logger;

    public FileDropIntake(
        IGameCatalogService gameCatalog,
        ISteamGameResolver steamResolver,
        IGameAutomationService automation,
        IConfigPathProvider configPath,
        ILogger<FileDropIntake> logger)
    {
        _gameCatalog = gameCatalog;
        _steamResolver = steamResolver;
        _automation = automation;
        _configPath = configPath;
        _logger = logger;
    }

    public async Task<DropAddResponse> HandleAsync(DropAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!FileDropHandler.LooksLikeFilePaths(request.Paths))
        {
            return new DropAddResponse
            {
                Success = false,
                Error = "Invalid drag-drop payload. Only existing .exe/.lnk/.url files are accepted."
            };
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var requests = new List<GameCatalogIntakeRequest>();
            var skipped = 0;
            foreach (var path in request.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var executablePath = ResolveExecutablePath(path);
                    if (string.IsNullOrWhiteSpace(executablePath) ||
                        !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    var (productName, _) = GameMetadataExtractor.ExtractMetadata(executablePath);
                    requests.Add(new GameCatalogIntakeRequest
                    {
                        Executable = ExecutableIdentity.Parse(executablePath),
                        ProductName = productName,
                        DisplayName = ResolveDisplayName(path, executablePath),
                        IsEnabled = true
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping dropped file {Path}", path);
                    skipped++;
                }
            }

            var results = _gameCatalog.BatchIntake(requests);
            if (results.Count > 0)
            {
                _automation.ReloadConfig();
            }

            var response = new DropAddResponse
            {
                Success = true,
                Added = results.Count(result => result.WasAdded),
                Updated = results.Count(result => !result.WasAdded),
                Skipped = skipped,
                ConfigPath = _configPath.ConfigPath
            };

            _logger.LogInformation(
                "File-drop Intake completed in {ElapsedMs}ms: Added={Added}, Updated={Updated}, Skipped={Skipped}",
                stopwatch.ElapsedMilliseconds,
                response.Added,
                response.Updated,
                response.Skipped);
            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File-drop Intake failed");
            return new DropAddResponse { Success = false, Error = ex.Message };
        }
        finally
        {
            stopwatch.Stop();
            _gate.Release();
        }
    }

    private string? ResolveExecutablePath(string path)
    {
        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = _steamResolver.TryParseInternetShortcutUrl(path);
            var appId = url is null ? null : _steamResolver.TryParseRunGameId(url);
            var resolved = appId is null ? null : _steamResolver.TryResolveExeFromAppId(appId);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return ExecutableResolver.TryResolveFromInput(path);
    }

    private static string ResolveDisplayName(string sourcePath, string executablePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var shortcutName = Path.GetFileNameWithoutExtension(sourcePath);
            if (!string.IsNullOrWhiteSpace(shortcutName))
            {
                return shortcutName;
            }
        }

        return Path.GetFileNameWithoutExtension(executablePath);
    }
}
