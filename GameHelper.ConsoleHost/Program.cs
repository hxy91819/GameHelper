using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Commands;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Infrastructure.Notifications;
using GameHelper.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

// Parse command line arguments
var parsedArgs = ArgumentParser.Parse(args);
using var backgroundOutput = parsedArgs.Background ? BackgroundConsoleOutput.Open(parsedArgs.ConfigOverride) : null;
if (!parsedArgs.Background) ConsoleEncoding.EnsureUtf8();
var isFileDropRequest = FileDropHandler.LooksLikeFilePaths(parsedArgs.EffectiveArgs);

// No command: open the WPF library, which connects to (or starts) the background runtime itself.
// Only a bare invocation (optionally with --config) takes this path; any other global option or
// sub-command keeps the console behavior below so it is not silently swallowed.
if (StartupModeResolver.ShouldOpenLibrary(parsedArgs))
{
    try
    {
        using var library = Process.Start(SystemTrayLogic.LibraryStartInfo(AppContext.BaseDirectory, parsedArgs.ConfigOverride, File.Exists));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to open the GameHelper library: {ex.Message}");
        Environment.Exit(1);
    }

    return;
}

var claimedSingleInstance = ProcessInstanceGuard.TryClaim();
var startupMode = StartupModeResolver.Resolve(isFileDropRequest, claimedSingleInstance);

if (startupMode == StartupMode.ForwardFileDropToRunningInstance)
{
    try
    {
        // IPC never changes the main process's configuration boundary; forwarded drops use its startup-selected config.
        var response = await FileDropIpcClient.SendAsync(parsedArgs.EffectiveArgs).ConfigureAwait(false);
        var text = FormatDropResponse(response);
        FileDropHandler.TryShowMessageBox(text, "GameHelper");
        Environment.Exit(response.Success ? 0 : 1);
    }
    catch (Exception ex)
    {
        FileDropHandler.TryShowMessageBox($"Failed to forward to the running instance: {ex.Message}", "GameHelper");
        Environment.Exit(1);
    }

    return;
}

if (startupMode == StartupMode.ExitAlreadyRunning)
{
    Console.WriteLine("GameHelper is already running; please do not start it again.");
    if (parsedArgs.EffectiveArgs.FirstOrDefault() == "storage") Environment.ExitCode = 1;
    return;
}

// Build host with dependency injection
var host = ConsoleHostBootstrapper.CreateBuilder(args, parsedArgs).Build();

// Print effective config file path and build info
try
{
    var cfgProvider = host.Services.GetService<IGameConfiguration>();
    if (cfgProvider is IConfigPathProvider pathProvider)
    {
        Console.WriteLine($"Using config: {pathProvider.ConfigPath}");
    }

    CommandHelpers.PrintBuildInfo(parsedArgs.EnableDebug);

    try
    {
        var persistentHost = !isFileDropRequest
            && parsedArgs.EffectiveArgs.Length > 0
            && parsedArgs.EffectiveArgs[0].Equals("monitor", StringComparison.OrdinalIgnoreCase);
        if (persistentHost)
        {
            var autoStartManager = host.Services.GetRequiredService<IAutoStartManager>();
            if (autoStartManager.IsSupported)
            {
                var appConfigProvider = host.Services.GetRequiredService<IGameConfiguration>();
                var appConfig = appConfigProvider.Read();
                autoStartManager.SetEnabled(appConfig.LaunchOnSystemStartup);
            }
        }
    }
    catch (InvalidDataException)
    {
        // The selected command will report actionable config errors if it needs the file.
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to apply auto-start preference: {ex.Message}");
    }

    // Handle file drag & drop (auto-add to config and exit)
    if (isFileDropRequest)
    {
        var config = host.Services.GetRequiredService<IGameConfiguration>();
        var configPath = ((IConfigPathProvider)config).ConfigPath;
        await host.Services.GetRequiredService<SessionStorage>().InitializeAsync(configPath, config.Read().Games,
            () => LegacyWriterGuard.EnsureDesktopStopped(configPath)).ConfigureAwait(false);
        var handler = host.Services.GetRequiredService<FileDropIntake>();
        var response = await handler.HandleAsync(
                new DropAddRequest { Paths = parsedArgs.EffectiveArgs },
                CancellationToken.None)
            .ConfigureAwait(false);

        var text = FormatDropResponse(response);
        FileDropHandler.TryShowMessageBox(text, "GameHelper");
        Environment.Exit(response.Success ? 0 : 1);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Auto-add failed: {ex.Message}");
    Environment.Exit(1);
}

// Execute the appropriate command
try
{
    if (parsedArgs.EffectiveArgs.FirstOrDefault()?.ToLowerInvariant() is "monitor" or "stats" or "sync")
    {
        var config = host.Services.GetRequiredService<IGameConfiguration>();
        var path = ((IConfigPathProvider)config).ConfigPath;
        await host.Services.GetRequiredService<SessionStorage>().InitializeAsync(path, config.Read().Games,
            () => LegacyWriterGuard.EnsureDesktopStopped(path)).ConfigureAwait(false);
    }
    await ConsoleCommandDispatcher.DispatchAsync(host, parsedArgs, CancellationToken.None).ConfigureAwait(false);
}
catch (InvalidDataException ex)
{
    Console.WriteLine($"Configuration error: {ex.Message}");
    Environment.Exit(1);
}

static string FormatDropResponse(DropAddResponse response)
{
    if (!response.Success)
    {
        return $"Add failed: {response.Error}";
    }

    return $"Add/update completed\nAdded={response.Added}, Updated={response.Updated}, Skipped={response.Skipped}\nConfig: {response.ConfigPath}";
}
