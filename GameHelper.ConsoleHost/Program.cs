using System;
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
var claimedSingleInstance = ProcessInstanceGuard.TryClaim();
var startupMode = StartupModeResolver.Resolve(isFileDropRequest, claimedSingleInstance);

if (startupMode == StartupMode.ForwardFileDropToRunningInstance)
{
    try
    {
        var response = await FileDropIpcClient.SendAsync(parsedArgs.EffectiveArgs, parsedArgs.ConfigOverride).ConfigureAwait(false);
        var text = FormatDropResponse(response);
        FileDropHandler.TryShowMessageBox(text, "GameHelper");
        Environment.Exit(response.Success ? 0 : 1);
    }
    catch (Exception ex)
    {
        FileDropHandler.TryShowMessageBox($"转发到运行中实例失败: {ex.Message}", "GameHelper");
        Environment.Exit(1);
    }

    return;
}

if (startupMode == StartupMode.ExitAlreadyRunning)
{
    Console.WriteLine("检测到 GameHelper 已在运行，请勿重复启动。");
    return;
}

// Build host with dependency injection
var host = ConsoleHostBootstrapper.CreateBuilder(args, parsedArgs).Build();

// Print effective config file path and build info
try
{
    var cfgProvider = host.Services.GetService<IConfigProvider>();
    if (cfgProvider is IConfigPathProvider pathProvider)
    {
        Console.WriteLine($"Using config: {pathProvider.ConfigPath}");
    }

    CommandHelpers.PrintBuildInfo(parsedArgs.EnableDebug);

    try
    {
        var autoStartManager = host.Services.GetRequiredService<IAutoStartManager>();
        if (autoStartManager.IsSupported)
        {
            var appConfigProvider = host.Services.GetRequiredService<IAppConfigProvider>();
            var appConfig = appConfigProvider.LoadAppConfig();
            autoStartManager.SetEnabled(appConfig.LaunchOnSystemStartup);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to apply auto-start preference: {ex.Message}");
    }

    // Handle file drag & drop (auto-add to config and exit)
    if (isFileDropRequest)
    {
        var handler = host.Services.GetRequiredService<IFileDropRequestHandler>();
        var response = await handler.HandleAsync(
                new DropAddRequest { Paths = parsedArgs.EffectiveArgs, ConfigOverride = parsedArgs.ConfigOverride },
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
await ConsoleCommandDispatcher.DispatchAsync(host, parsedArgs, CancellationToken.None).ConfigureAwait(false);

static string FormatDropResponse(DropAddResponse response)
{
    if (!response.Success)
    {
        return $"添加失败: {response.Error}";
    }

    return $"已完成添加/更新\nAdded={response.Added}, Updated={response.Updated}, Skipped={response.Skipped}\n重复清理: {response.DuplicatesRemoved}\n配置: {response.ConfigPath}";
}

