using System;
using System.IO;
using System.Threading;
using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Commands;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

ConsoleEncoding.EnsureUtf8();

// Parse command line arguments
var parsedArgs = ArgumentParser.Parse(args);
var isFileDropRequest = FileDropHandler.LooksLikeFilePaths(parsedArgs.EffectiveArgs);

// 启动早期就把交互窗口交给 Windows Terminal 托管：开机自启、桌面快捷方式、脚本拉起
// 都走同一条路径，用户看到的字体与窗口样式因此保持一致。无法满足条件时留在当前宿主。
if (WindowsTerminalHost.TryRelaunch(args, isFileDropRequest))
{
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
        var persistentHost = !isFileDropRequest && (parsedArgs.UseInteractiveShell || parsedArgs.EffectiveArgs.Length == 0
            || parsedArgs.EffectiveArgs[0].Equals("monitor", StringComparison.OrdinalIgnoreCase)
            || parsedArgs.EffectiveArgs[0].Equals("interactive", StringComparison.OrdinalIgnoreCase));
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
